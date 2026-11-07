using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TruyenCuaToi;

public class MainForm : Form
{
    private static readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(45) };

    private readonly WebView2 webView;

    private volatile string? rootFolderPath;
    private volatile string selectedBackground = "";
    private volatile string geminiApiKey = "";
    private volatile string geminiModel = "gemini-3.5-flash-lite";
    private volatile string translateTargetLang = "Tiếng Việt";
    private volatile string translateCustomPrompt = "";
    private volatile bool lanServerEnabled = false;
    private volatile string trayHideHotkey = "Alt+X";

    private readonly string appDataLocalDir;
    private readonly string settingsFilePath;
    private readonly string mobileAuthFilePath;
    private readonly string wwwrootPath;
    private readonly SemaphoreSlim fileWriteLock = new(1, 1);

    // Thumbnail cache (.thumbs) concurrency controls
    private const string ThumbsDirName = ".thumbs";
    private const int ThumbMaxWidth = 420;
    private const int ThumbMaxHeight = 630;
    private const long ThumbJpegQuality = 82L;
    private readonly SemaphoreSlim thumbCpuSemaphore = new(Math.Clamp(Environment.ProcessorCount, 2, 6));
    private readonly ConcurrentDictionary<string, SemaphoreSlim> thumbKeyLocks = new();
    private static readonly ImageCodecInfo? JpegCodecInfo = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

    // System Tray & Global Hotkey state
    private const int WM_HOTKEY = 0x0312;
    private const int TRAY_HOTKEY_ID = 0x5443;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;

    private HotkeyMessageWindow? hotkeyWindow;
    private NotifyIcon? trayIcon;
    private ContextMenuStrip? trayMenu;
    private Icon? customAppIcon;
    private IntPtr customAppIconHandle = IntPtr.Zero;
    private volatile bool isTrayHidden = false;
    private volatile bool isRecordingHotkey = false;
    private volatile bool isHotkeyRegistered = false;
    private volatile bool isExitingApp = false;
    private volatile bool isWebViewLoaded = false;
    private volatile bool pendingOpenConnectionSettings = false;

    private readonly object authConfigLock = new();
    private volatile bool lanPasswordEnabled = false;
    private byte[] lanPasswordSalt = Array.Empty<byte>();
    private byte[] lanPasswordHash = Array.Empty<byte>();
    private byte[] lanTokenSecret = Array.Empty<byte>();
    private readonly ConcurrentDictionary<string, (int FailCount, DateTimeOffset LockedUntil)> loginRateLimits = new();

    private const string AuthCookieName = "tct_mobile_auth";
    private const int Pbkdf2Iterations = 100_000;
    private const int MaxFailedLoginAttempts = 5;
    private const int LockoutDurationSeconds = 20;

    // Localhost-only server for PC WebView2 UI (always running while app is open)
    private TcpListener? localTcpListener;
    private CancellationTokenSource? localServerCts;
    private int localServerPort;

    // Optional Wi-Fi / LAN server for Mobile clients (toggled in Settings modal)
    private readonly object lanServerLock = new();
    private TcpListener? lanTcpListener;
    private CancellationTokenSource? lanServerCts;
    private int lanServerPort;

    // State for Fullscreen restore
    private FormBorderStyle prevBorderStyle = FormBorderStyle.Sizable;
    private FormWindowState prevWindowState = FormWindowState.Normal;
    private Rectangle prevBounds;
    private bool isFullScreen = false;

    private static readonly Regex MediaRegex = new(@"\.(jpe?g|jfif|png|gif|webp|avif|bmp|mp4|webm)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VideoRegex = new(@"\.(mp4|webm)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BgFileRegex = new(@"^background_\d+\.(jpe?g|jfif|png|webp|avif|gif|bmp)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ArchiveRegex = new(@"\.(zip|cbz)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private const string ArchiveDelimiter = "::";

    private const string AppDataDirName = "App data";

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string psz1, string psz2);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static readonly Comparer<string> NaturalStringComparer = Comparer<string>.Create((a, b) => StrCmpLogicalW(a ?? "", b ?? ""));

    private sealed class HotkeyMessageWindow : NativeWindow, IDisposable
    {
        private readonly Action onHotkey;

        public HotkeyMessageWindow(Action onHotkeyCallback)
        {
            onHotkey = onHotkeyCallback;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == TRAY_HOTKEY_ID)
            {
                onHotkey();
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                DestroyHandle();
            }
        }
    }

    public MainForm()
    {
        Text = "Truyện của toi";
        Width = 1360;
        Height = 860;
        MinimumSize = new Size(800, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(17, 24, 39); // #111827
        Icon = CreateCustomAppIcon();

        wwwrootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (!Directory.Exists(wwwrootPath))
        {
            Directory.CreateDirectory(wwwrootPath);
        }

        appDataLocalDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TruyenCuaToi"
        );
        Directory.CreateDirectory(appDataLocalDir);
        settingsFilePath = Path.Combine(appDataLocalDir, "app-settings.json");
        mobileAuthFilePath = Path.Combine(appDataLocalDir, "mobile-auth.json");

        LoadSavedSettings();
        LoadLanAuthConfig();
        InitializeSystemTrayAndHotkey();
        StartLocalLoopbackServer();

        if (lanServerEnabled)
        {
            SetLanServerEnabled(true);
        }

        webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.FromArgb(17, 24, 39)
        };
        Controls.Add(webView);

        Load += async (_, _) => await InitializeWebViewAsync();
        FormClosing += MainForm_FormClosing;
    }

    private Icon CreateCustomAppIcon()
    {
        try
        {
            using var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                using var bgBrush = new LinearGradientBrush(
                    new Rectangle(0, 0, 32, 32),
                    Color.FromArgb(37, 99, 235),
                    Color.FromArgb(15, 23, 42),
                    LinearGradientMode.ForwardDiagonal);
                using var borderPen = new Pen(Color.FromArgb(96, 165, 250), 1.5f);

                using var path = new GraphicsPath();
                int r = 7;
                path.AddArc(1, 1, r * 2, r * 2, 180, 90);
                path.AddArc(31 - r * 2, 1, r * 2, r * 2, 270, 90);
                path.AddArc(31 - r * 2, 31 - r * 2, r * 2, r * 2, 0, 90);
                path.AddArc(1, 31 - r * 2, r * 2, r * 2, 90, 90);
                path.CloseFigure();

                g.FillPath(bgBrush, path);
                g.DrawPath(borderPen, path);

                using var font = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("T", font, Brushes.White, new RectangleF(0, 1, 32, 31), sf);
            }

            customAppIconHandle = bmp.GetHicon();
            customAppIcon = Icon.FromHandle(customAppIconHandle);
            return customAppIcon;
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    private void InitializeSystemTrayAndHotkey()
    {
        trayMenu = new ContextMenuStrip();
        var openAppItem = new ToolStripMenuItem("Mở App");
        openAppItem.Font = new Font(trayMenu.Font, FontStyle.Bold);
        openAppItem.Click += (_, _) => RestoreFromSystemTray(openConnectionSettings: false);

        var openConnSettingsItem = new ToolStripMenuItem("Mở Cài Đặt Kết Nối");
        openConnSettingsItem.Click += (_, _) => RestoreFromSystemTray(openConnectionSettings: true);

        var stopAppItem = new ToolStripMenuItem("Dừng");
        stopAppItem.Click += (_, _) => ExitApplicationFromTray();

        trayMenu.Items.Add(openAppItem);
        trayMenu.Items.Add(openConnSettingsItem);
        trayMenu.Items.Add(stopAppItem);

        trayIcon = new NotifyIcon
        {
            Icon = Icon ?? SystemIcons.Application,
            Text = "Truyện của toi",
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        trayIcon.DoubleClick += (_, _) => RestoreFromSystemTray(openConnectionSettings: false);

        hotkeyWindow = new HotkeyMessageWindow(OnGlobalTrayHotkeyPressed);
        ApplyTrayHotkeyOnUiThread();
    }

    private void OnGlobalTrayHotkeyPressed()
    {
        // Phím tắt chỉ dùng để ẩn xuống System Tray khi cửa sổ đang hiển thị (không bật hiện lại)
        if (isExitingApp || IsDisposed || isRecordingHotkey || isTrayHidden || !Visible)
            return;

        HideToSystemTray();
    }

    private void HideToSystemTray()
    {
        if (IsDisposed || isExitingApp) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(HideToSystemTray));
            return;
        }

        isTrayHidden = true;
        if (trayIcon != null)
        {
            trayIcon.Visible = true;
        }
        Hide();
    }

    private void RestoreFromSystemTray(bool openConnectionSettings)
    {
        if (IsDisposed || isExitingApp) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => RestoreFromSystemTray(openConnectionSettings)));
            return;
        }

        isTrayHidden = false;
        if (!Visible)
        {
            Show();
        }

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = isFullScreen ? FormWindowState.Maximized : FormWindowState.Normal;
        }

        Activate();
        BringToFront();
        if (Handle != IntPtr.Zero)
        {
            SetForegroundWindow(Handle);
        }

        if (openConnectionSettings)
        {
            TriggerOpenConnectionSettingsInWebView();
        }
    }

    private void TriggerOpenConnectionSettingsInWebView()
    {
        if (IsDisposed) return;
        if (!isWebViewLoaded || webView?.CoreWebView2 == null)
        {
            pendingOpenConnectionSettings = true;
            return;
        }

        try
        {
            _ = webView.CoreWebView2.ExecuteScriptAsync("window.openConnectionSettingsFromTray && window.openConnectionSettingsFromTray();");
        }
        catch { }
    }

    private void ExitApplicationFromTray()
    {
        if (IsDisposed || isExitingApp) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(ExitApplicationFromTray));
            return;
        }

        isExitingApp = true;
        Close();
    }

    private bool ApplyTrayHotkeyOnUiThread()
    {
        if (IsDisposed) return false;
        if (InvokeRequired)
        {
            return Invoke(new Func<bool>(ApplyTrayHotkeyOnUiThread));
        }

        if (hotkeyWindow == null || hotkeyWindow.Handle == IntPtr.Zero)
        {
            isHotkeyRegistered = false;
            return false;
        }

        try { UnregisterHotKey(hotkeyWindow.Handle, TRAY_HOTKEY_ID); } catch { }

        string rawSpec = (trayHideHotkey ?? "").Trim();
        if (string.IsNullOrEmpty(rawSpec))
        {
            trayHideHotkey = "";
            isHotkeyRegistered = false;
            return true;
        }

        if (!TryParseHotkeySpec(rawSpec, out uint modifiers, out uint vk, out string normalizedSpec))
        {
            isHotkeyRegistered = false;
            return false;
        }

        trayHideHotkey = normalizedSpec;
        bool ok = RegisterHotKey(hotkeyWindow.Handle, TRAY_HOTKEY_ID, modifiers | MOD_NOREPEAT, vk);
        if (!ok)
        {
            ok = RegisterHotKey(hotkeyWindow.Handle, TRAY_HOTKEY_ID, modifiers, vk);
        }

        isHotkeyRegistered = ok;
        return ok;
    }

    private static bool TryParseHotkeySpec(string spec, out uint modifiers, out uint vk, out string normalized)
    {
        modifiers = 0;
        vk = 0;
        normalized = "";

        if (string.IsNullOrWhiteSpace(spec)) return false;

        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        bool hasCtrl = false, hasAlt = false, hasShift = false;
        string? mainToken = null;

        foreach (var part in parts)
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                hasCtrl = true;
            }
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                hasAlt = true;
            }
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                hasShift = true;
            }
            else
            {
                mainToken = part;
            }
        }

        if (string.IsNullOrWhiteSpace(mainToken)) return false;

        if (!TryMapKeyTokenToVk(mainToken, out vk, out string canonicalKeyName, out bool isFunctionOrSpecialKey))
        {
            return false;
        }

        // Yêu cầu ít nhất Ctrl hoặc Alt trừ khi là phím chức năng F1..F12 / Pause / Insert
        if (!hasCtrl && !hasAlt && !isFunctionOrSpecialKey)
        {
            return false;
        }

        if (hasCtrl) modifiers |= MOD_CONTROL;
        if (hasAlt) modifiers |= MOD_ALT;
        if (hasShift) modifiers |= MOD_SHIFT;

        var normParts = new List<string>();
        if (hasCtrl) normParts.Add("Ctrl");
        if (hasAlt) normParts.Add("Alt");
        if (hasShift) normParts.Add("Shift");
        normParts.Add(canonicalKeyName);
        normalized = string.Join("+", normParts);
        return true;
    }

    private static bool TryMapKeyTokenToVk(string token, out uint vk, out string canonical, out bool isFunctionOrSpecialKey)
    {
        vk = 0;
        canonical = "";
        isFunctionOrSpecialKey = false;

        string t = token.Trim();
        if (t.Length == 1)
        {
            char ch = char.ToUpperInvariant(t[0]);
            if (ch >= 'A' && ch <= 'Z')
            {
                vk = ch;
                canonical = ch.ToString();
                return true;
            }
            if (ch >= '0' && ch <= '9')
            {
                vk = ch;
                canonical = ch.ToString();
                return true;
            }
        }

        if (t.Length >= 2 && (t[0] == 'F' || t[0] == 'f') && int.TryParse(t.Substring(1), out int fNum) && fNum >= 1 && fNum <= 12)
        {
            vk = (uint)(0x70 + (fNum - 1));
            canonical = $"F{fNum}";
            isFunctionOrSpecialKey = true;
            return true;
        }

        switch (t.ToLowerInvariant())
        {
            case "space": vk = 0x20; canonical = "Space"; return true;
            case "tab": vk = 0x09; canonical = "Tab"; return true;
            case "esc":
            case "escape": vk = 0x1B; canonical = "Esc"; return true;
            case "`":
            case "~":
            case "backquote": vk = 0xC0; canonical = "`"; return true;
            case "-":
            case "minus": vk = 0xBD; canonical = "-"; return true;
            case "=":
            case "equal": vk = 0xBB; canonical = "="; return true;
            case "[":
            case "bracketleft": vk = 0xDB; canonical = "["; return true;
            case "]":
            case "bracketright": vk = 0xDD; canonical = "]"; return true;
            case "\\":
            case "backslash": vk = 0xDC; canonical = "\\"; return true;
            case ";":
            case "semicolon": vk = 0xBA; canonical = ";"; return true;
            case "'":
            case "quote": vk = 0xDE; canonical = "'"; return true;
            case ",":
            case "comma": vk = 0xBC; canonical = ","; return true;
            case ".":
            case "period": vk = 0xBE; canonical = "."; return true;
            case "/":
            case "slash": vk = 0xBF; canonical = "/"; return true;
            case "insert":
            case "ins": vk = 0x2D; canonical = "Insert"; isFunctionOrSpecialKey = true; return true;
            case "delete":
            case "del": vk = 0x2E; canonical = "Delete"; return true;
            case "home": vk = 0x24; canonical = "Home"; return true;
            case "end": vk = 0x23; canonical = "End"; return true;
            case "pageup":
            case "pgup": vk = 0x21; canonical = "PageUp"; return true;
            case "pagedown":
            case "pgdn": vk = 0x22; canonical = "PageDown"; return true;
            case "pause": vk = 0x13; canonical = "Pause"; isFunctionOrSpecialKey = true; return true;
            default:
                return false;
        }
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        isExitingApp = true;

        try
        {
            if (hotkeyWindow != null && hotkeyWindow.Handle != IntPtr.Zero)
            {
                UnregisterHotKey(hotkeyWindow.Handle, TRAY_HOTKEY_ID);
            }
            hotkeyWindow?.Dispose();
            hotkeyWindow = null;
        }
        catch { }

        try
        {
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayIcon = null;
            }
            trayMenu?.Dispose();
            trayMenu = null;
        }
        catch { }

        try
        {
            customAppIcon?.Dispose();
            customAppIcon = null;
            if (customAppIconHandle != IntPtr.Zero)
            {
                DestroyIcon(customAppIconHandle);
                customAppIconHandle = IntPtr.Zero;
            }
        }
        catch { }

        try
        {
            localServerCts?.Cancel();
            localTcpListener?.Stop();
        }
        catch { }

        SetLanServerEnabled(false, persistSetting: false);
    }

    private void StartLocalLoopbackServer()
    {
        localServerCts = new CancellationTokenSource();

        // Dùng dải port 49871..49874 để giữ nguyên localStorage/cấu hình giao diện của bản PC trước đây
        int[] candidatePorts = [49871, 49872, 49873, 49874, 0];
        foreach (int candidate in candidatePorts)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, candidate);
                listener.Start();
                localTcpListener = listener;
                localServerPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                break;
            }
            catch
            {
                // Try next port
            }
        }

        if (localTcpListener != null)
        {
            _ = Task.Run(() => AcceptTcpClientsLoopAsync(localTcpListener, isLanListener: false, localServerCts.Token));
        }
    }

    private bool IsLanServerRunning()
    {
        lock (lanServerLock)
        {
            return lanTcpListener != null && lanServerPort > 0;
        }
    }

    private void SetLanServerEnabled(bool enabled, bool persistSetting = true)
    {
        lock (lanServerLock)
        {
            if (enabled)
            {
                if (lanTcpListener == null)
                {
                    var cts = new CancellationTokenSource();
                    int[] candidatePorts = [49880, 49881, 49882, 49883, 49885, 0];
                    foreach (int candidate in candidatePorts)
                    {
                        try
                        {
                            var listener = new TcpListener(IPAddress.Any, candidate);
                            listener.Start();
                            lanTcpListener = listener;
                            lanServerCts = cts;
                            lanServerPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                            _ = Task.Run(() => AcceptTcpClientsLoopAsync(listener, isLanListener: true, cts.Token));
                            break;
                        }
                        catch
                        {
                            // Try next port
                        }
                    }

                    if (lanTcpListener == null)
                    {
                        cts.Dispose();
                        throw new InvalidOperationException("Không thể mở cổng kết nối mạng LAN trên máy tính này.");
                    }
                }
                lanServerEnabled = true;
            }
            else
            {
                try { lanServerCts?.Cancel(); } catch { }
                try { lanTcpListener?.Stop(); } catch { }
                try { lanServerCts?.Dispose(); } catch { }
                lanServerCts = null;
                lanTcpListener = null;
                lanServerPort = 0;
                if (persistSetting)
                {
                    lanServerEnabled = false;
                }
            }
        }

        if (persistSetting)
        {
            SaveSettings();
        }
    }

    private List<string> GetLanIpAddresses()
    {
        var ips = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                var props = ni.GetIPProperties();
                bool hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(g.Address));

                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                    {
                        string ipStr = ua.Address.ToString();
                        if (ipStr.StartsWith("169.254.")) continue; // Skip APIPA
                        if (hasGateway)
                            ips.Insert(0, ipStr);
                        else
                            ips.Add(ipStr);
                    }
                }
            }
        }
        catch
        {
            // Fallback
        }

        if (ips.Count == 0)
        {
            try
            {
                foreach (var addr in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (addr.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr))
                    {
                        ips.Add(addr.ToString());
                    }
                }
            }
            catch { }
        }

        return ips.Distinct().ToList();
    }

    private string GetPrimaryLanUrl()
    {
        int port = lanServerPort;
        if (port <= 0) return "";
        var ips = GetLanIpAddresses();
        string ip = ips.FirstOrDefault() ?? "127.0.0.1";
        return $"http://{ip}:{port}";
    }

    private List<string> GetAllLanUrls()
    {
        int port = lanServerPort;
        if (port <= 0) return new List<string>();
        var urls = GetLanIpAddresses().Select(ip => $"http://{ip}:{port}").ToList();
        urls.Add($"http://127.0.0.1:{port}");
        return urls.Distinct().ToList();
    }

    private async Task AcceptTcpClientsLoopAsync(TcpListener listener, bool isLanListener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleTcpClientAsync(client, isLanListener, ct), ct);
            }
            catch
            {
                if (ct.IsCancellationRequested) break;
            }
        }
    }

    private async Task HandleTcpClientAsync(TcpClient client, bool isLanListener, CancellationToken ct)
    {
        using (client)
        {
            client.ReceiveTimeout = 30000;
            client.SendTimeout = 30000;
            try
            {
                bool isLocalHost = !isLanListener && IsLoopbackEndpoint(client.Client.RemoteEndPoint);
                string remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";

                using var stream = client.GetStream();

                // Read HTTP headers up to \r\n\r\n (max 64KB)
                byte[] headerBuf = new byte[65536];
                int totalRead = 0;
                int headerEndIdx = -1;

                while (totalRead < headerBuf.Length)
                {
                    int read = await stream.ReadAsync(headerBuf.AsMemory(totalRead, headerBuf.Length - totalRead), ct);
                    if (read <= 0) return;
                    totalRead += read;

                    headerEndIdx = FindHeaderEnd(headerBuf, totalRead);
                    if (headerEndIdx >= 0) break;
                }

                if (headerEndIdx < 0) return;

                string headerText = Encoding.ASCII.GetString(headerBuf, 0, headerEndIdx);
                var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) return;

                var reqLineParts = lines[0].Split(' ', 3);
                if (reqLineParts.Length < 2) return;

                string httpMethod = reqLineParts[0].ToUpperInvariant();
                string rawTarget = reqLineParts[1];

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < lines.Length; i++)
                {
                    int colon = lines[i].IndexOf(':');
                    if (colon > 0)
                    {
                        string k = lines[i].Substring(0, colon).Trim();
                        string v = lines[i].Substring(colon + 1).Trim();
                        headers[k] = v;
                    }
                }

                if (httpMethod == "OPTIONS")
                {
                    await WriteHttpResponseHeadersAsync(stream, "204 No Content", "text/plain", 0, noCache: true);
                    return;
                }

                int bodyStartOffset = headerEndIdx + 4;
                int alreadyReadBodyBytes = totalRead - bodyStartOffset;
                long contentLength = 0;
                if (headers.TryGetValue("Content-Length", out var clStr))
                {
                    long.TryParse(clStr, out contentLength);
                }

                string path = rawTarget;
                string? rawQuery = null;
                int qIdx = rawTarget.IndexOf('?');
                if (qIdx >= 0)
                {
                    path = rawTarget.Substring(0, qIdx);
                    rawQuery = rawTarget.Substring(qIdx + 1);
                }

                if (httpMethod == "GET" && path.Equals("/api/auth/status", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonResponseAsync(stream, 200, new
                    {
                        ok = true,
                        passwordEnabled = lanPasswordEnabled,
                        authenticated = IsRequestAuthenticated(headers, isLocalHost),
                        isLocalHost
                    });
                    return;
                }

                if (httpMethod == "POST" && path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase))
                {
                    if (contentLength < 0 || contentLength > 64 * 1024)
                    {
                        await WriteJsonResponseAsync(stream, 400, new { ok = false, error = "Dữ liệu không hợp lệ." });
                        return;
                    }

                    string loginJson = await ReadHttpRequestBodyAsync(stream, headerBuf, bodyStartOffset, alreadyReadBodyBytes, (int)contentLength, ct);
                    await HandleAuthLoginHttpAsync(stream, loginJson, remoteIp, isLocalHost);
                    return;
                }

                if (httpMethod == "POST" && path.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase))
                {
                    string clearCookie = $"Set-Cookie: {AuthCookieName}=; Path=/; Max-Age=0; HttpOnly; SameSite=Lax\r\n";
                    await WriteJsonResponseAsync(stream, 200, new { ok = true }, extraHeaders: clearCookie);
                    return;
                }

                if (httpMethod == "POST" && path.Equals("/api/rpc", StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsRequestAuthenticated(headers, isLocalHost))
                    {
                        await WriteJsonResponseAsync(stream, 401, new { ok = false, unauthorized = true, error = "Vui lòng nhập mật khẩu để kết nối." });
                        return;
                    }

                    if (contentLength < 0 || contentLength > 25 * 1024 * 1024)
                    {
                        await WriteJsonResponseAsync(stream, 400, new { ok = false, error = "Payload quá lớn." });
                        return;
                    }

                    string bodyJson = await ReadHttpRequestBodyAsync(stream, headerBuf, bodyStartOffset, alreadyReadBodyBytes, (int)contentLength, ct);
                    await HandleHttpRpcAsync(stream, bodyJson, isLocalHost);
                    return;
                }

                if (path == "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
                {
                    string targetHtml = isLanListener ? "index-mobile.html" : "index-pc.html";
                    var indexFile = Path.Combine(wwwrootPath, targetHtml);
                    await ServeStaticFileOverTcpAsync(stream, httpMethod, indexFile, "text/html; charset=utf-8", noCache: true);
                    return;
                }

                if (path.Equals("/index-pc.html", StringComparison.OrdinalIgnoreCase) || path.Equals("/pc", StringComparison.OrdinalIgnoreCase))
                {
                    var pcFile = Path.Combine(wwwrootPath, "index-pc.html");
                    await ServeStaticFileOverTcpAsync(stream, httpMethod, pcFile, "text/html; charset=utf-8", noCache: true);
                    return;
                }

                if (path.Equals("/index-mobile.html", StringComparison.OrdinalIgnoreCase) || path.Equals("/mobile", StringComparison.OrdinalIgnoreCase))
                {
                    var mobileFile = Path.Combine(wwwrootPath, "index-mobile.html");
                    await ServeStaticFileOverTcpAsync(stream, httpMethod, mobileFile, "text/html; charset=utf-8", noCache: true);
                    return;
                }

                if (path.Equals("/tailwind.js", StringComparison.OrdinalIgnoreCase))
                {
                    var jsFile = Path.Combine(wwwrootPath, "tailwind.js");
                    await ServeStaticFileOverTcpAsync(stream, httpMethod, jsFile, "application/javascript; charset=utf-8", noCache: false);
                    return;
                }

                if (path.Equals("/manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    var manifestFile = Path.Combine(wwwrootPath, "manifest.json");
                    await ServeStaticFileOverTcpAsync(stream, httpMethod, manifestFile, "application/manifest+json; charset=utf-8", noCache: true);
                    return;
                }

                if (path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteHttpResponseHeadersAsync(stream, "204 No Content", "image/x-icon", 0, noCache: false);
                    return;
                }

                if (path.Equals("/thumb", StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsRequestAuthenticated(headers, isLocalHost))
                    {
                        await WriteHttpResponseHeadersAsync(stream, "401 Unauthorized", "text/plain; charset=utf-8", 0, noCache: true);
                        return;
                    }

                    string? relPath = GetQueryParamRaw(rawQuery, "path");
                    headers.TryGetValue("Range", out string? rangeHeader);
                    await HandleThumbRequestAsync(stream, httpMethod, relPath, rangeHeader);
                    return;
                }

                if (path.Equals("/media", StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsRequestAuthenticated(headers, isLocalHost))
                    {
                        await WriteHttpResponseHeadersAsync(stream, "401 Unauthorized", "text/plain; charset=utf-8", 0, noCache: true);
                        return;
                    }

                    string? relPath = GetQueryParamRaw(rawQuery, "path");
                    var currentRoot = rootFolderPath;

                    if (string.IsNullOrWhiteSpace(relPath) || string.IsNullOrWhiteSpace(currentRoot))
                    {
                        await WriteHttpResponseHeadersAsync(stream, "404 Not Found", "text/plain", 0, noCache: true);
                        return;
                    }

                    var rootFull = Path.GetFullPath(currentRoot);
                    headers.TryGetValue("Range", out string? rangeHeader);

                    int archiveSepIdx = relPath.IndexOf(ArchiveDelimiter, StringComparison.Ordinal);
                    if (archiveSepIdx > 0)
                    {
                        string archiveRel = relPath.Substring(0, archiveSepIdx);
                        string entryFullName = relPath.Substring(archiveSepIdx + ArchiveDelimiter.Length).Replace('\\', '/').TrimStart('/');
                        var normalizedArchiveRel = archiveRel.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
                        var archiveFull = Path.GetFullPath(Path.Combine(rootFull, normalizedArchiveRel));

                        if (!archiveFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ||
                            !ArchiveRegex.IsMatch(archiveFull) ||
                            !File.Exists(archiveFull) ||
                            string.IsNullOrWhiteSpace(entryFullName))
                        {
                            await WriteHttpResponseHeadersAsync(stream, "404 Not Found", "text/plain", 0, noCache: true);
                            return;
                        }

                        await ServeArchiveEntryOverTcpWithRangeAsync(stream, httpMethod, archiveFull, entryFullName, rangeHeader);
                        return;
                    }

                    var normalizedRel = relPath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
                    var targetFull = Path.GetFullPath(Path.Combine(rootFull, normalizedRel));

                    if (!targetFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(targetFull))
                    {
                        await WriteHttpResponseHeadersAsync(stream, "404 Not Found", "text/plain", 0, noCache: true);
                        return;
                    }

                    string contentType = GetMimeType(targetFull);
                    await ServeMediaFileOverTcpWithRangeAsync(stream, httpMethod, targetFull, contentType, rangeHeader);
                    return;
                }

                await WriteHttpResponseHeadersAsync(stream, "404 Not Found", "text/plain", 0, noCache: true);
            }
            catch
            {
                // Ignore client disconnect/abort errors
            }
        }
    }

    private static async Task<string> ReadHttpRequestBodyAsync(
        Stream stream,
        byte[] headerBuf,
        int bodyStartOffset,
        int alreadyReadBodyBytes,
        int contentLength,
        CancellationToken ct)
    {
        if (contentLength <= 0) return "";

        byte[] bodyBytes = new byte[contentLength];
        if (alreadyReadBodyBytes > 0)
        {
            int copyLen = Math.Min(alreadyReadBodyBytes, contentLength);
            Buffer.BlockCopy(headerBuf, bodyStartOffset, bodyBytes, 0, copyLen);
        }

        int offset = Math.Min(Math.Max(0, alreadyReadBodyBytes), contentLength);
        while (offset < contentLength)
        {
            int r = await stream.ReadAsync(bodyBytes.AsMemory(offset, contentLength - offset), ct);
            if (r <= 0) break;
            offset += r;
        }

        return Encoding.UTF8.GetString(bodyBytes, 0, offset);
    }

    private static int FindHeaderEnd(byte[] buf, int length)
    {
        for (int i = 0; i <= length - 4; i++)
        {
            if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
            {
                return i;
            }
        }
        return -1;
    }

    private static async Task WriteHttpResponseHeadersAsync(
        Stream stream,
        string status,
        string contentType,
        long contentLength,
        bool noCache,
        string? extraHeaders = null)
    {
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {status}\r\n");
        sb.Append($"Content-Type: {contentType}\r\n");
        sb.Append($"Content-Length: {contentLength}\r\n");
        sb.Append("Access-Control-Allow-Origin: *\r\n");
        sb.Append("Access-Control-Allow-Methods: GET, POST, HEAD, OPTIONS\r\n");
        sb.Append("Access-Control-Allow-Headers: Content-Type, Range\r\n");
        sb.Append($"Cache-Control: {(noCache ? "no-cache, no-store, must-revalidate" : "public, max-age=86400")}\r\n");
        if (!string.IsNullOrEmpty(extraHeaders))
        {
            sb.Append(extraHeaders);
        }
        sb.Append("Connection: close\r\n\r\n");

        byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(headerBytes);
    }

    private static async Task WriteJsonResponseAsync(Stream stream, int statusCode, object payload, string? extraHeaders = null)
    {
        string statusText = statusCode switch
        {
            200 => "200 OK",
            400 => "400 Bad Request",
            401 => "401 Unauthorized",
            429 => "429 Too Many Requests",
            _ => $"{statusCode} Error"
        };
        byte[] jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await WriteHttpResponseHeadersAsync(stream, statusText, "application/json; charset=utf-8", jsonBytes.Length, noCache: true, extraHeaders: extraHeaders);
        await stream.WriteAsync(jsonBytes);
    }

    private static async Task ServeStaticFileOverTcpAsync(Stream stream, string httpMethod, string filePath, string contentType, bool noCache)
    {
        if (!File.Exists(filePath))
        {
            await WriteHttpResponseHeadersAsync(stream, "404 Not Found", "text/plain", 0, noCache: true);
            return;
        }

        byte[] bytes = await File.ReadAllBytesAsync(filePath);
        await WriteHttpResponseHeadersAsync(stream, "200 OK", contentType, bytes.Length, noCache);
        if (!httpMethod.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            await stream.WriteAsync(bytes);
        }
    }

    private static async Task ServeMediaFileOverTcpWithRangeAsync(
        Stream stream,
        string httpMethod,
        string filePath,
        string contentType,
        string? rangeHeader)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, useAsync: true);
        long totalLength = fs.Length;
        bool isHead = httpMethod.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) && totalLength > 0)
        {
            var rangeSpec = rangeHeader.Substring("bytes=".Length).Split(',')[0].Trim();
            var dashIdx = rangeSpec.IndexOf('-');
            if (dashIdx >= 0)
            {
                string startStr = rangeSpec.Substring(0, dashIdx).Trim();
                string endStr = rangeSpec.Substring(dashIdx + 1).Trim();

                long start = string.IsNullOrEmpty(startStr) ? 0 : long.Parse(startStr);
                long end = string.IsNullOrEmpty(endStr) ? (totalLength - 1) : long.Parse(endStr);

                if (start < 0) start = 0;
                if (end >= totalLength) end = totalLength - 1;

                if (start <= end)
                {
                    long count = end - start + 1;
                    string extra = $"Accept-Ranges: bytes\r\nContent-Range: bytes {start}-{end}/{totalLength}\r\n";
                    await WriteHttpResponseHeadersAsync(stream, "206 Partial Content", contentType, count, noCache: false, extraHeaders: extra);

                    if (!isHead)
                    {
                        fs.Seek(start, SeekOrigin.Begin);
                        await CopyBoundedStreamAsync(fs, stream, count);
                    }
                    return;
                }
            }
        }

        await WriteHttpResponseHeadersAsync(stream, "200 OK", contentType, totalLength, noCache: false, extraHeaders: "Accept-Ranges: bytes\r\n");
        if (!isHead)
        {
            await fs.CopyToAsync(stream, 65536);
        }
    }

    private static async Task ServeArchiveEntryOverTcpWithRangeAsync(
        Stream stream,
        string httpMethod,
        string archiveFullPath,
        string entryFullName,
        string? rangeHeader)
    {
        using var fs = new FileStream(archiveFullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, useAsync: true);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

        var entry = zip.GetEntry(entryFullName)
            ?? zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').TrimStart('/').Equals(entryFullName, StringComparison.OrdinalIgnoreCase));

        if (entry == null || string.IsNullOrEmpty(entry.Name) || entry.Name.StartsWith('.') || entry.Name.StartsWith('~') || !MediaRegex.IsMatch(entry.Name))
        {
            await WriteHttpResponseHeadersAsync(stream, "404 Not Found", "text/plain", 0, noCache: true);
            return;
        }

        string contentType = GetMimeType(entry.Name);
        long totalLength = entry.Length;
        bool isHead = httpMethod.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) && totalLength > 0)
        {
            var rangeSpec = rangeHeader.Substring("bytes=".Length).Split(',')[0].Trim();
            var dashIdx = rangeSpec.IndexOf('-');
            if (dashIdx >= 0)
            {
                string startStr = rangeSpec.Substring(0, dashIdx).Trim();
                string endStr = rangeSpec.Substring(dashIdx + 1).Trim();

                long start = string.IsNullOrEmpty(startStr) ? 0 : long.Parse(startStr);
                long end = string.IsNullOrEmpty(endStr) ? (totalLength - 1) : long.Parse(endStr);

                if (start < 0) start = 0;
                if (end >= totalLength) end = totalLength - 1;

                if (start <= end)
                {
                    long count = end - start + 1;
                    string extra = $"Accept-Ranges: bytes\r\nContent-Range: bytes {start}-{end}/{totalLength}\r\n";
                    await WriteHttpResponseHeadersAsync(stream, "206 Partial Content", contentType, count, noCache: false, extraHeaders: extra);

                    if (!isHead)
                    {
                        using var entryStream = entry.Open();
                        await SkipStreamBytesAsync(entryStream, start);
                        await CopyBoundedStreamAsync(entryStream, stream, count);
                    }
                    return;
                }
            }
        }

        await WriteHttpResponseHeadersAsync(stream, "200 OK", contentType, totalLength, noCache: false, extraHeaders: "Accept-Ranges: bytes\r\n");
        if (!isHead)
        {
            using var entryStream = entry.Open();
            await entryStream.CopyToAsync(stream, 65536);
        }
    }

    private static async Task SkipStreamBytesAsync(Stream input, long bytesToSkip)
    {
        if (bytesToSkip <= 0) return;
        if (input.CanSeek)
        {
            input.Seek(bytesToSkip, SeekOrigin.Current);
            return;
        }
        byte[] skipBuf = new byte[65536];
        long remaining = bytesToSkip;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(skipBuf.Length, remaining);
            int read = await input.ReadAsync(skipBuf.AsMemory(0, toRead));
            if (read <= 0) break;
            remaining -= read;
        }
    }

    private static async Task CopyBoundedStreamAsync(Stream input, Stream output, long bytesToCopy)
    {
        byte[] buffer = new byte[65536];
        long remaining = bytesToCopy;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = await input.ReadAsync(buffer.AsMemory(0, toRead));
            if (read <= 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read));
            remaining -= read;
        }
    }

    private static string? GetQueryParamRaw(string? query, string key)
    {
        if (string.IsNullOrEmpty(query)) return null;
        var q = query.TrimStart('?');
        var parts = q.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            int eqIdx = part.IndexOf('=');
            if (eqIdx <= 0) continue;
            var k = part.Substring(0, eqIdx);
            if (k.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                var rawVal = part.Substring(eqIdx + 1);
                return Uri.UnescapeDataString(rawVal);
            }
        }
        return null;
    }

    private static string GetMimeType(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" or ".jfif" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".avif" => "image/avif",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            _ => "application/octet-stream"
        };
    }

    private async Task HandleThumbRequestAsync(Stream stream, string httpMethod, string? relPath, string? rangeHeader)
    {
        var currentRoot = rootFolderPath;
        if (string.IsNullOrWhiteSpace(relPath) || string.IsNullOrWhiteSpace(currentRoot))
        {
            await WriteHttpResponseHeadersAsync(stream, "404 Not Found", "text/plain", 0, noCache: true);
            return;
        }

        var rootFull = Path.GetFullPath(currentRoot);
        var thumbsDir = Path.Combine(rootFull, AppDataDirName, ThumbsDirName);

        int archiveSepIdx = relPath.IndexOf(ArchiveDelimiter, StringComparison.Ordinal);
        if (archiveSepIdx > 0)
        {
            string archiveRel = relPath.Substring(0, archiveSepIdx);
            string entryFullName = relPath.Substring(archiveSepIdx + ArchiveDelimiter.Length).Replace('\\', '/').TrimStart('/');
            var normalizedArchiveRel = archiveRel.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            var archiveFull = Path.GetFullPath(Path.Combine(rootFull, normalizedArchiveRel));

            if (!archiveFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ||
                !ArchiveRegex.IsMatch(archiveFull) ||
                !File.Exists(archiveFull) ||
                string.IsNullOrWhiteSpace(entryFullName))
            {
                await WriteHttpResponseHeadersAsync(stream, "404 Not Found", "text/plain", 0, noCache: true);
                return;
            }

            if (VideoRegex.IsMatch(entryFullName))
            {
                await ServeArchiveEntryOverTcpWithRangeAsync(stream, httpMethod, archiveFull, entryFullName, rangeHeader);
                return;
            }

            var afi = new FileInfo(archiveFull);
            string cacheKey = ComputeThumbCacheKey($"zip|{archiveRel.Replace('\\', '/')}|{entryFullName}|{afi.Length}|{afi.LastWriteTimeUtc.Ticks}");
            string thumbCachePath = Path.Combine(thumbsDir, cacheKey + ".jpg");

            if (File.Exists(thumbCachePath))
            {
                await ServeStaticFileOverTcpAsync(stream, httpMethod, thumbCachePath, "image/jpeg", noCache: false);
                return;
            }

            bool generated = await EnsureThumbGeneratedAsync(cacheKey, thumbCachePath, thumbsDir, () =>
                TryBuildThumbFromArchiveEntry(archiveFull, entryFullName, thumbCachePath));

            if (generated && File.Exists(thumbCachePath))
            {
                await ServeStaticFileOverTcpAsync(stream, httpMethod, thumbCachePath, "image/jpeg", noCache: false);
                return;
            }

            await ServeArchiveEntryOverTcpWithRangeAsync(stream, httpMethod, archiveFull, entryFullName, rangeHeader);
            return;
        }

        var normalizedRel = relPath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var targetFull = Path.GetFullPath(Path.Combine(rootFull, normalizedRel));

        if (!targetFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(targetFull))
        {
            await WriteHttpResponseHeadersAsync(stream, "404 Not Found", "text/plain", 0, noCache: true);
            return;
        }

        if (VideoRegex.IsMatch(targetFull))
        {
            await ServeMediaFileOverTcpWithRangeAsync(stream, httpMethod, targetFull, GetMimeType(targetFull), rangeHeader);
            return;
        }

        var fi = new FileInfo(targetFull);
        string fileCacheKey = ComputeThumbCacheKey($"file|{relPath.Replace('\\', '/')}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}");
        string fileThumbCachePath = Path.Combine(thumbsDir, fileCacheKey + ".jpg");

        if (File.Exists(fileThumbCachePath))
        {
            await ServeStaticFileOverTcpAsync(stream, httpMethod, fileThumbCachePath, "image/jpeg", noCache: false);
            return;
        }

        bool fileGenerated = await EnsureThumbGeneratedAsync(fileCacheKey, fileThumbCachePath, thumbsDir, () =>
            TryBuildThumbFromFile(targetFull, fileThumbCachePath));

        if (fileGenerated && File.Exists(fileThumbCachePath))
        {
            await ServeStaticFileOverTcpAsync(stream, httpMethod, fileThumbCachePath, "image/jpeg", noCache: false);
            return;
        }

        await ServeMediaFileOverTcpWithRangeAsync(stream, httpMethod, targetFull, GetMimeType(targetFull), rangeHeader);
    }

    private static string ComputeThumbCacheKey(string rawSignature)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawSignature));
        return Convert.ToHexString(hash).Substring(0, 32).ToLowerInvariant();
    }

    private async Task<bool> EnsureThumbGeneratedAsync(string cacheKey, string thumbCachePath, string thumbsDir, Func<bool> buildFn)
    {
        var keyLock = thumbKeyLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync();
        try
        {
            if (File.Exists(thumbCachePath)) return true;

            Directory.CreateDirectory(thumbsDir);
            await thumbCpuSemaphore.WaitAsync();
            try
            {
                return await Task.Run(buildFn);
            }
            finally
            {
                thumbCpuSemaphore.Release();
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            keyLock.Release();
        }
    }

    private static bool TryBuildThumbFromFile(string sourceFilePath, string destThumbPath)
    {
        try
        {
            using var fs = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return TryEncodeStreamToThumbFile(fs, destThumbPath);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryBuildThumbFromArchiveEntry(string archiveFullPath, string entryFullName, string destThumbPath)
    {
        try
        {
            using var fs = new FileStream(archiveFullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
            var entry = zip.GetEntry(entryFullName)
                ?? zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').TrimStart('/').Equals(entryFullName, StringComparison.OrdinalIgnoreCase));

            if (entry == null || string.IsNullOrEmpty(entry.Name) || entry.Length <= 0 || entry.Length > 100 * 1024 * 1024)
                return false;

            using var ms = new MemoryStream((int)Math.Min(entry.Length, 16 * 1024 * 1024));
            using (var es = entry.Open())
            {
                es.CopyTo(ms);
            }
            ms.Position = 0;
            return TryEncodeStreamToThumbFile(ms, destThumbPath);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryEncodeStreamToThumbFile(Stream seekableStream, string destThumbPath)
    {
        string tmpPath = $"{destThumbPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            bool ok = TryEncodeResizedJpegGdiPlus(seekableStream, tmpPath);
            if (!ok && seekableStream.CanSeek)
            {
                seekableStream.Position = 0;
                ok = TryEncodeResizedJpegWic(seekableStream, tmpPath);
            }

            if (ok && File.Exists(tmpPath))
            {
                File.Move(tmpPath, destThumbPath, overwrite: true);
                return true;
            }
        }
        catch
        {
            // Ignore and clean up tmp
        }
        finally
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
        }
        return false;
    }

    private static bool TryEncodeResizedJpegGdiPlus(Stream input, string outputPath)
    {
        try
        {
            using var img = Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: false);
            ApplyExifOrientationIfPresent(img);

            int srcW = img.Width;
            int srcH = img.Height;
            if (srcW <= 0 || srcH <= 0) return false;

            double scale = Math.Min(1.0, Math.Min((double)ThumbMaxWidth / srcW, (double)ThumbMaxHeight / srcH));
            int dstW = Math.Max(1, (int)Math.Round(srcW * scale));
            int dstH = Math.Max(1, (int)Math.Round(srcH * scale));

            using var canvas = new Bitmap(dstW, dstH, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.FromArgb(17, 24, 39));
                g.CompositingQuality = CompositingQuality.HighSpeed;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(img, new Rectangle(0, 0, dstW, dstH));
            }

            if (JpegCodecInfo != null)
            {
                using var encParams = new EncoderParameters(1);
                encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, ThumbJpegQuality);
                canvas.Save(outputPath, JpegCodecInfo, encParams);
            }
            else
            {
                canvas.Save(outputPath, ImageFormat.Jpeg);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyExifOrientationIfPresent(Image img)
    {
        try
        {
            const int ExifOrientationId = 0x0112;
            if (!img.PropertyIdList.Contains(ExifOrientationId)) return;
            var prop = img.GetPropertyItem(ExifOrientationId);
            if (prop?.Value == null || prop.Value.Length < 2) return;
            int val = BitConverter.ToUInt16(prop.Value, 0);
            var rot = val switch
            {
                3 => RotateFlipType.Rotate180FlipNone,
                6 => RotateFlipType.Rotate90FlipNone,
                8 => RotateFlipType.Rotate270FlipNone,
                _ => RotateFlipType.RotateNoneFlipNone
            };
            if (rot != RotateFlipType.RotateNoneFlipNone)
            {
                img.RotateFlip(rot);
            }
        }
        catch { }
    }

    private static bool TryEncodeResizedJpegWic(Stream input, string outputPath)
    {
        try
        {
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                input,
                System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0) return false;
            System.Windows.Media.Imaging.BitmapSource source = decoder.Frames[0];
            int srcW = source.PixelWidth;
            int srcH = source.PixelHeight;
            if (srcW <= 0 || srcH <= 0) return false;

            double scale = Math.Min(1.0, Math.Min((double)ThumbMaxWidth / srcW, (double)ThumbMaxHeight / srcH));
            if (scale < 0.999)
            {
                var transformed = new System.Windows.Media.Imaging.TransformedBitmap(
                    source,
                    new System.Windows.Media.ScaleTransform(scale, scale));
                transformed.Freeze();
                source = transformed;
            }

            var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder
            {
                QualityLevel = (int)ThumbJpegQuality
            };
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));

            using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            encoder.Save(outFs);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void LoadSavedSettings()
    {
        try
        {
            if (File.Exists(settingsFilePath))
            {
                var json = File.ReadAllText(settingsFilePath, Encoding.UTF8);
                var node = JsonNode.Parse(json);
                var savedPath = node?["rootFolderPath"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(savedPath) && Directory.Exists(savedPath))
                {
                    rootFolderPath = savedPath;
                }

                var savedBg = node?["selectedBackground"]?.GetValue<string>();
                if (savedBg != null) selectedBackground = savedBg;

                var savedKey = node?["geminiApiKey"]?.GetValue<string>();
                if (savedKey != null) geminiApiKey = savedKey;

                var savedModel = node?["geminiModel"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(savedModel)) geminiModel = savedModel;

                var savedLang = node?["translateTargetLang"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(savedLang)) translateTargetLang = savedLang;

                var savedPrompt = node?["translateCustomPrompt"]?.GetValue<string>();
                if (savedPrompt != null) translateCustomPrompt = savedPrompt;

                var savedLanEnabled = node?["lanServerEnabled"]?.GetValue<bool>();
                if (savedLanEnabled.HasValue) lanServerEnabled = savedLanEnabled.Value;

                if (node is JsonObject obj && obj.ContainsKey("trayHideHotkey"))
                {
                    trayHideHotkey = node["trayHideHotkey"]?.GetValue<string>() ?? "";
                }
            }
        }
        catch
        {
            // Ignore corrupted settings file
        }
    }

    private void SaveSettings()
    {
        try
        {
            JsonObject rootObj = new();
            if (File.Exists(settingsFilePath))
            {
                try
                {
                    var existing = JsonNode.Parse(File.ReadAllText(settingsFilePath, Encoding.UTF8)) as JsonObject;
                    if (existing != null) rootObj = existing;
                }
                catch { }
            }

            rootObj["rootFolderPath"] = rootFolderPath;
            rootObj["selectedBackground"] = selectedBackground;
            rootObj["geminiApiKey"] = geminiApiKey;
            rootObj["geminiModel"] = geminiModel;
            rootObj["translateTargetLang"] = translateTargetLang;
            rootObj["translateCustomPrompt"] = translateCustomPrompt;
            rootObj["lanServerEnabled"] = lanServerEnabled;
            rootObj["trayHideHotkey"] = trayHideHotkey;

            File.WriteAllText(settingsFilePath, rootObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        }
        catch
        {
            // Ignore settings write failure
        }
    }

    private static bool IsLoopbackEndpoint(EndPoint? ep)
    {
        if (ep is not IPEndPoint ipEp) return false;
        if (IPAddress.IsLoopback(ipEp.Address)) return true;
        if (ipEp.Address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(ipEp.Address.MapToIPv4())) return true;
        return false;
    }

    private void LoadLanAuthConfig()
    {
        lock (authConfigLock)
        {
            try
            {
                if (File.Exists(mobileAuthFilePath))
                {
                    var json = File.ReadAllText(mobileAuthFilePath, Encoding.UTF8);
                    var node = JsonNode.Parse(json);
                    bool enabled = node?["enabled"]?.GetValue<bool>() == true;
                    string? saltB64 = node?["saltBase64"]?.GetValue<string>();
                    string? hashB64 = node?["hashBase64"]?.GetValue<string>();
                    string? secretB64 = node?["tokenSecretBase64"]?.GetValue<string>();

                    if (!string.IsNullOrWhiteSpace(saltB64) && !string.IsNullOrWhiteSpace(hashB64))
                    {
                        lanPasswordSalt = Convert.FromBase64String(saltB64);
                        lanPasswordHash = Convert.FromBase64String(hashB64);
                    }

                    if (!string.IsNullOrWhiteSpace(secretB64))
                    {
                        lanTokenSecret = Convert.FromBase64String(secretB64);
                    }

                    if (lanTokenSecret.Length < 16)
                    {
                        lanTokenSecret = RandomNumberGenerator.GetBytes(32);
                    }

                    lanPasswordEnabled = enabled && lanPasswordSalt.Length >= 16 && lanPasswordHash.Length >= 32;
                    return;
                }
            }
            catch
            {
                // Ignore corrupted auth file
            }

            lanPasswordEnabled = false;
            lanTokenSecret = RandomNumberGenerator.GetBytes(32);
        }
    }

    private void SaveLanAuthConfigLocked()
    {
        try
        {
            var obj = new
            {
                enabled = lanPasswordEnabled,
                saltBase64 = lanPasswordSalt.Length > 0 ? Convert.ToBase64String(lanPasswordSalt) : "",
                hashBase64 = lanPasswordHash.Length > 0 ? Convert.ToBase64String(lanPasswordHash) : "",
                tokenSecretBase64 = Convert.ToBase64String(lanTokenSecret)
            };
            File.WriteAllText(
                mobileAuthFilePath,
                JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8
            );
        }
        catch
        {
            // Ignore write failure
        }
    }

    private bool HasSavedLanPassword()
    {
        lock (authConfigLock)
        {
            return lanPasswordSalt.Length >= 16 && lanPasswordHash.Length >= 32;
        }
    }

    private void ConfigureLanPassword(bool enabled, string? newPassword)
    {
        lock (authConfigLock)
        {
            if (enabled)
            {
                if (!string.IsNullOrEmpty(newPassword))
                {
                    byte[] salt = RandomNumberGenerator.GetBytes(16);
                    byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
                        Encoding.UTF8.GetBytes(newPassword),
                        salt,
                        Pbkdf2Iterations,
                        HashAlgorithmName.SHA256,
                        32
                    );
                    lanPasswordSalt = salt;
                    lanPasswordHash = hash;
                }
                else if (lanPasswordSalt.Length < 16 || lanPasswordHash.Length < 32)
                {
                    throw new ArgumentException("Vui lòng nhập mật khẩu mới trước khi bật bảo vệ.");
                }

                lanTokenSecret = RandomNumberGenerator.GetBytes(32);
                lanPasswordEnabled = true;
            }
            else
            {
                lanPasswordEnabled = false;
                lanTokenSecret = RandomNumberGenerator.GetBytes(32);
            }

            loginRateLimits.Clear();
            SaveLanAuthConfigLocked();
        }
    }

    private bool VerifyLanPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return false;

        byte[] salt;
        byte[] expectedHash;
        lock (authConfigLock)
        {
            if (!lanPasswordEnabled || lanPasswordSalt.Length < 16 || lanPasswordHash.Length < 32)
                return false;
            salt = lanPasswordSalt;
            expectedHash = lanPasswordHash;
        }

        byte[] computed = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            expectedHash.Length
        );
        return CryptographicOperations.FixedTimeEquals(computed, expectedHash);
    }

    private string CreateSessionToken()
    {
        byte[] secret;
        lock (authConfigLock)
        {
            if (lanTokenSecret.Length < 16)
            {
                lanTokenSecret = RandomNumberGenerator.GetBytes(32);
            }
            secret = lanTokenSecret;
        }

        long exp = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        byte[] sig = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes($"v1:{exp}"));
        return $"{exp}.{Convert.ToHexString(sig)}";
    }

    private bool VerifySessionToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        int dotIdx = token.IndexOf('.');
        if (dotIdx <= 0 || dotIdx >= token.Length - 1) return false;

        string expStr = token.Substring(0, dotIdx);
        string sigHex = token.Substring(dotIdx + 1);
        if (sigHex.Length != 64 || !long.TryParse(expStr, out long exp)) return false;
        if (exp <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;

        byte[] actualSig;
        try
        {
            actualSig = Convert.FromHexString(sigHex);
        }
        catch
        {
            return false;
        }

        byte[] secret;
        lock (authConfigLock)
        {
            secret = lanTokenSecret;
        }
        if (secret.Length < 16) return false;

        byte[] expectedSig = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes($"v1:{exp}"));
        return CryptographicOperations.FixedTimeEquals(actualSig, expectedSig);
    }

    private static string? GetCookieValue(Dictionary<string, string> headers, string cookieName)
    {
        if (!headers.TryGetValue("Cookie", out var rawCookies) || string.IsNullOrWhiteSpace(rawCookies))
            return null;

        var parts = rawCookies.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            int eqIdx = trimmed.IndexOf('=');
            if (eqIdx <= 0) continue;
            var k = trimmed.Substring(0, eqIdx).Trim();
            if (k.Equals(cookieName, StringComparison.Ordinal))
            {
                return trimmed.Substring(eqIdx + 1).Trim();
            }
        }
        return null;
    }

    private bool IsRequestAuthenticated(Dictionary<string, string> headers, bool isLocalHost)
    {
        if (isLocalHost) return true;
        if (!lanPasswordEnabled) return true;
        string? token = GetCookieValue(headers, AuthCookieName);
        return VerifySessionToken(token);
    }

    private async Task HandleAuthLoginHttpAsync(Stream stream, string bodyJson, string remoteIp, bool isLocalHost)
    {
        if (!lanPasswordEnabled || isLocalHost)
        {
            await WriteJsonResponseAsync(stream, 200, new { ok = true, authenticated = true });
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (loginRateLimits.TryGetValue(remoteIp, out var rateInfo) && rateInfo.LockedUntil > now)
        {
            int waitSec = Math.Max(1, (int)Math.Ceiling((rateInfo.LockedUntil - now).TotalSeconds));
            await WriteJsonResponseAsync(stream, 429, new
            {
                ok = false,
                lockedSeconds = waitSec,
                error = $"Nhập sai quá nhiều lần. Vui lòng thử lại sau {waitSec} giây."
            });
            return;
        }

        string password = "";
        try
        {
            var node = JsonNode.Parse(bodyJson);
            password = node?["password"]?.GetValue<string>() ?? "";
        }
        catch
        {
            // Ignore malformed JSON
        }

        if (VerifyLanPassword(password))
        {
            loginRateLimits.TryRemove(remoteIp, out _);
            string sessionToken = CreateSessionToken();
            string setCookie = $"Set-Cookie: {AuthCookieName}={sessionToken}; Path=/; Max-Age=2592000; HttpOnly; SameSite=Lax\r\n";
            await WriteJsonResponseAsync(stream, 200, new { ok = true, authenticated = true }, extraHeaders: setCookie);
            return;
        }

        var updated = loginRateLimits.AddOrUpdate(
            remoteIp,
            _ => (1, DateTimeOffset.MinValue),
            (_, prev) =>
            {
                int nextCount = (prev.LockedUntil > DateTimeOffset.MinValue && prev.LockedUntil <= now) ? 1 : prev.FailCount + 1;
                if (nextCount >= MaxFailedLoginAttempts)
                {
                    return (0, now.AddSeconds(LockoutDurationSeconds));
                }
                return (nextCount, DateTimeOffset.MinValue);
            }
        );

        if (updated.LockedUntil > now)
        {
            await WriteJsonResponseAsync(stream, 429, new
            {
                ok = false,
                lockedSeconds = LockoutDurationSeconds,
                error = $"Nhập sai {MaxFailedLoginAttempts} lần liên tiếp. Tạm khóa {LockoutDurationSeconds} giây."
            });
            return;
        }

        int remainingAttempts = Math.Max(1, MaxFailedLoginAttempts - updated.FailCount);
        await WriteJsonResponseAsync(stream, 401, new
        {
            ok = false,
            error = $"Mật khẩu không đúng (còn {remainingAttempts} lần thử)."
        });
    }

    private async Task InitializeWebViewAsync()
    {
        var userDataFolder = Path.Combine(appDataLocalDir, "WebView2Data");
        var envOptions = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required --disable-features=msWebOOUI,msPdfOOUI"
        };
        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, envOptions);
        await webView.EnsureCoreWebView2Async(env);

        var settings = webView.CoreWebView2.Settings;
        settings.AreDefaultContextMenusEnabled = true;
        settings.IsZoomControlEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = true;

        webView.CoreWebView2.ContainsFullScreenElementChanged += CoreWebView2_ContainsFullScreenElementChanged;
        webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
        webView.CoreWebView2.NavigationCompleted += (_, _) =>
        {
            isWebViewLoaded = true;
            if (pendingOpenConnectionSettings)
            {
                pendingOpenConnectionSettings = false;
                TriggerOpenConnectionSettingsInWebView();
            }
        };

        webView.CoreWebView2.Navigate($"http://127.0.0.1:{localServerPort}/index-pc.html");
    }

    private void SetWindowFullScreen(bool full)
    {
        if (full && !isFullScreen)
        {
            isFullScreen = true;
            prevBorderStyle = FormBorderStyle;
            prevWindowState = WindowState;
            prevBounds = Bounds;

            FormBorderStyle = FormBorderStyle.None;
            if (WindowState == FormWindowState.Maximized)
            {
                WindowState = FormWindowState.Normal;
            }
            WindowState = FormWindowState.Maximized;
        }
        else if (!full && isFullScreen)
        {
            isFullScreen = false;
            FormBorderStyle = prevBorderStyle;
            WindowState = prevWindowState;
            if (prevWindowState == FormWindowState.Normal)
            {
                Bounds = prevBounds;
            }
        }
    }

    private object HandleToggleWindowFullscreen()
    {
        if (InvokeRequired)
        {
            return Invoke(new Func<object>(HandleToggleWindowFullscreen));
        }
        SetWindowFullScreen(!isFullScreen);
        return new { isFullScreen };
    }

    private void CoreWebView2_ContainsFullScreenElementChanged(object? sender, object e)
    {
        SetWindowFullScreen(webView.CoreWebView2.ContainsFullScreenElement);
    }

    private object HandleGetTrayHotkeyConfig(bool isLocalHost)
    {
        return new
        {
            hotkey = trayHideHotkey,
            registered = isHotkeyRegistered,
            isLocalHost
        };
    }

    private object HandleSetTrayHotkey(JsonNode? @params, bool isLocalHost)
    {
        if (!isLocalHost)
        {
            throw new UnauthorizedAccessException("Chỉ có thể đặt phím tắt ẩn cửa sổ trực tiếp trên máy chủ PC.");
        }

        string nextHotkey = @params?["hotkey"]?.GetValue<string>()?.Trim() ?? "";
        if (!string.IsNullOrEmpty(nextHotkey) && !TryParseHotkeySpec(nextHotkey, out _, out _, out string normalized))
        {
            throw new ArgumentException("Tổ hợp phím không hợp lệ. Vui lòng kết hợp với Ctrl / Alt (hoặc dùng phím F1..F12).");
        }
        else if (!string.IsNullOrEmpty(nextHotkey))
        {
            TryParseHotkeySpec(nextHotkey, out _, out _, out nextHotkey);
        }

        trayHideHotkey = nextHotkey;
        bool ok = ApplyTrayHotkeyOnUiThread();
        SaveSettings();

        return new
        {
            hotkey = trayHideHotkey,
            registered = ok
        };
    }

    private object HandleSetTrayHotkeyRecording(JsonNode? @params, bool isLocalHost)
    {
        if (isLocalHost)
        {
            isRecordingHotkey = @params?["recording"]?.GetValue<bool>() == true;
        }
        return new { recording = isRecordingHotkey };
    }

    private object HandleHideToTrayNow(bool isLocalHost)
    {
        if (!isLocalHost)
        {
            throw new UnauthorizedAccessException("Chỉ hỗ trợ ẩn cửa sổ trên PC.");
        }
        HideToSystemTray();
        return new { hidden = true };
    }

    private object HandleGetThumbCacheInfo()
    {
        var current = rootFolderPath;
        if (string.IsNullOrWhiteSpace(current) || !Directory.Exists(current))
        {
            return new { count = 0, bytes = 0L };
        }

        var thumbsDir = Path.Combine(current, AppDataDirName, ThumbsDirName);
        if (!Directory.Exists(thumbsDir))
        {
            return new { count = 0, bytes = 0L };
        }

        int count = 0;
        long bytes = 0;
        try
        {
            foreach (var f in new DirectoryInfo(thumbsDir).EnumerateFiles("*.jpg"))
            {
                count++;
                bytes += f.Length;
            }
        }
        catch { }

        return new { count, bytes };
    }

    private object HandleClearThumbCache()
    {
        var current = rootFolderPath;
        if (string.IsNullOrWhiteSpace(current) || !Directory.Exists(current))
        {
            return new { deletedCount = 0, count = 0, bytes = 0L };
        }

        var thumbsDir = Path.Combine(current, AppDataDirName, ThumbsDirName);
        if (!Directory.Exists(thumbsDir))
        {
            return new { deletedCount = 0, count = 0, bytes = 0L };
        }

        int deleted = 0;
        try
        {
            foreach (var f in new DirectoryInfo(thumbsDir).EnumerateFiles())
            {
                try
                {
                    f.Delete();
                    deleted++;
                }
                catch { }
            }
        }
        catch { }

        return new { deletedCount = deleted, count = 0, bytes = 0L };
    }

    private async Task<object?> ExecuteRpcMethodAsync(string method, JsonNode? @params, bool isLocalHost)
    {
        return method switch
        {
            "getServerInfo" => HandleGetServerInfo(isLocalHost),
            "setLanServerEnabled" => HandleSetLanServerEnabled(@params, isLocalHost),
            "getLanPasswordConfig" => HandleGetLanPasswordConfig(isLocalHost),
            "setLanPassword" => HandleSetLanPassword(@params, isLocalHost),
            "getTrayHotkeyConfig" => HandleGetTrayHotkeyConfig(isLocalHost),
            "setTrayHotkey" => HandleSetTrayHotkey(@params, isLocalHost),
            "setTrayHotkeyRecording" => HandleSetTrayHotkeyRecording(@params, isLocalHost),
            "hideToTrayNow" => HandleHideToTrayNow(isLocalHost),
            "getThumbCacheInfo" => await Task.Run(() => HandleGetThumbCacheInfo()),
            "clearThumbCache" => await Task.Run(() => HandleClearThumbCache()),
            "copyToClipboard" => HandleCopyToClipboard(@params, isLocalHost),
            "getSavedRootFolder" => HandleGetSavedRootFolder(),
            "selectRootFolder" => HandleSelectRootFolderOnUiThread(isLocalHost),
            "setRootFolderPath" => HandleSetRootFolderPath(@params, isLocalHost),
            "toggleWindowFullscreen" => HandleToggleWindowFullscreen(),
            "loadAppData" => await Task.Run(() => HandleLoadAppData()),
            "saveAppDataFile" => await HandleSaveAppDataFileAsync(@params),
            "scanDirectory" => await Task.Run(() => HandleScanDirectory(@params)),
            "getStoryFiles" => await Task.Run(() => HandleGetStoryFiles(@params)),
            "loadAvatars" => await Task.Run(() => HandleLoadAvatars()),
            "loadBackgrounds" => await Task.Run(() => HandleLoadBackgrounds()),
            "getSelectedBackground" => HandleGetSelectedBackground(),
            "saveSelectedBackground" => HandleSaveSelectedBackground(@params),
            "getTranslateSettings" => HandleGetTranslateSettings(),
            "saveTranslateSettings" => HandleSaveTranslateSettings(@params),
            "translateImage" => await HandleTranslateImageAsync(@params),
            _ => throw new InvalidOperationException($"Unknown method: {method}")
        };
    }

    private async Task HandleHttpRpcAsync(Stream stream, string bodyJson, bool isLocalHost)
    {
        try
        {
            var msg = JsonNode.Parse(bodyJson);
            var method = msg?["method"]?.GetValue<string>();
            var @params = msg?["params"];

            if (string.IsNullOrEmpty(method))
            {
                await WriteJsonResponseAsync(stream, 400, new { ok = false, error = "Thiếu method." });
                return;
            }

            object? result = await ExecuteRpcMethodAsync(method, @params, isLocalHost);
            await WriteJsonResponseAsync(stream, 200, new { ok = true, result });
        }
        catch (Exception ex)
        {
            await WriteJsonResponseAsync(stream, 200, new { ok = false, error = ex.Message });
        }
    }

    private async void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? reqId = null;
        try
        {
            var raw = e.TryGetWebMessageAsString();
            if (string.IsNullOrWhiteSpace(raw)) return;

            var msg = JsonNode.Parse(raw);
            reqId = msg?["reqId"]?.GetValue<string>();
            var method = msg?["method"]?.GetValue<string>();
            var @params = msg?["params"];

            if (string.IsNullOrEmpty(method)) return;

            object? result = await ExecuteRpcMethodAsync(method, @params, isLocalHost: true);
            SendSuccess(reqId, result);
        }
        catch (Exception ex)
        {
            SendError(reqId, ex.Message);
        }
    }

    private void SendSuccess(string? reqId, object? result)
    {
        if (string.IsNullOrEmpty(reqId) || IsDisposed) return;
        var payload = JsonSerializer.Serialize(new { reqId, ok = true, result });
        if (InvokeRequired)
        {
            BeginInvoke(() => SendJsonSafe(payload));
        }
        else
        {
            SendJsonSafe(payload);
        }
    }

    private void SendError(string? reqId, string error)
    {
        if (string.IsNullOrEmpty(reqId) || IsDisposed) return;
        var payload = JsonSerializer.Serialize(new { reqId, ok = false, error });
        if (InvokeRequired)
        {
            BeginInvoke(() => SendJsonSafe(payload));
        }
        else
        {
            SendJsonSafe(payload);
        }
    }

    private void SendJsonSafe(string jsonPayload)
    {
        try
        {
            if (!IsDisposed && webView.CoreWebView2 != null)
            {
                webView.CoreWebView2.PostWebMessageAsJson(jsonPayload);
            }
        }
        catch
        {
            // Ignore if closing
        }
    }

    private void NotifyDesktopWebViewFolderChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(NotifyDesktopWebViewFolderChanged));
            return;
        }
        try
        {
            if (webView?.CoreWebView2 != null)
            {
                _ = webView.CoreWebView2.ExecuteScriptAsync("window.reloadFromHostFolderChange && window.reloadFromHostFolderChange();");
            }
        }
        catch { }
    }

    private object HandleGetServerInfo(bool isLocalHost)
    {
        bool active = IsLanServerRunning();
        return new
        {
            lanServerEnabled = active,
            port = active ? lanServerPort : 0,
            primaryLanUrl = active ? GetPrimaryLanUrl() : "",
            lanUrls = active ? GetAllLanUrls() : new List<string>(),
            rootFolder = HandleGetSavedRootFolder(),
            passwordEnabled = lanPasswordEnabled,
            hasSavedPassword = HasSavedLanPassword(),
            isLocalHost
        };
    }

    private object HandleSetLanServerEnabled(JsonNode? @params, bool isLocalHost)
    {
        if (!isLocalHost)
        {
            throw new UnauthorizedAccessException("Chỉ có thể bật hoặc tắt kết nối mạng LAN trực tiếp trên máy chủ PC.");
        }

        bool enabled = @params?["enabled"]?.GetValue<bool>() == true;
        SetLanServerEnabled(enabled, persistSetting: true);
        return HandleGetServerInfo(true);
    }

    private object HandleCopyToClipboard(JsonNode? @params, bool isLocalHost)
    {
        if (!isLocalHost)
        {
            return new { copied = false };
        }

        string text = @params?["text"]?.GetValue<string>() ?? "";
        if (string.IsNullOrEmpty(text)) return new { copied = false };

        if (InvokeRequired)
        {
            return Invoke(new Func<object>(() => HandleCopyToClipboard(@params, isLocalHost)));
        }

        try
        {
            Clipboard.SetText(text);
            return new { copied = true };
        }
        catch
        {
            return new { copied = false };
        }
    }

    private object HandleGetLanPasswordConfig(bool isLocalHost)
    {
        return new
        {
            enabled = lanPasswordEnabled,
            hasSavedPassword = HasSavedLanPassword(),
            isLocalHost
        };
    }

    private object HandleSetLanPassword(JsonNode? @params, bool isLocalHost)
    {
        if (!isLocalHost)
        {
            throw new UnauthorizedAccessException("Chỉ có thể đặt hoặc tắt mật khẩu trực tiếp trên máy chủ PC.");
        }

        bool enabled = @params?["enabled"]?.GetValue<bool>() == true;
        string? password = @params?["password"]?.GetValue<string>()?.Trim();
        ConfigureLanPassword(enabled, password);

        return new
        {
            enabled = lanPasswordEnabled,
            hasSavedPassword = HasSavedLanPassword()
        };
    }

    private object? HandleGetSavedRootFolder()
    {
        LoadSavedSettings();
        var current = rootFolderPath;
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
        {
            var dirInfo = new DirectoryInfo(current);
            return new
            {
                path = current,
                name = dirInfo.Name
            };
        }
        return null;
    }

    private object? HandleSetRootFolderPath(JsonNode? @params, bool isLocalHost)
    {
        var inputPath = @params?["path"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(inputPath) || !Directory.Exists(inputPath))
        {
            throw new ArgumentException("Đường dẫn thư mục không tồn tại trên máy chủ PC.");
        }
        rootFolderPath = inputPath;
        SaveSettings();
        if (!isLocalHost)
        {
            NotifyDesktopWebViewFolderChanged();
        }
        var dirInfo = new DirectoryInfo(inputPath);
        return new
        {
            aborted = false,
            path = inputPath,
            name = dirInfo.Name
        };
    }

    private object? HandleSelectRootFolderOnUiThread(bool isLocalHost)
    {
        if (InvokeRequired)
        {
            return Invoke(new Func<object?>(() => HandleSelectRootFolderOnUiThread(isLocalHost)));
        }

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }
        Activate();

        using var dialog = new FolderBrowserDialog
        {
            Description = "Chọn thư mục gốc chứa truyện của bạn",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        var current = rootFolderPath;
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        var res = dialog.ShowDialog(this);
        if (res == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath) && Directory.Exists(dialog.SelectedPath))
        {
            rootFolderPath = dialog.SelectedPath;
            SaveSettings();
            if (!isLocalHost)
            {
                NotifyDesktopWebViewFolderChanged();
            }
            var dirInfo = new DirectoryInfo(dialog.SelectedPath);
            return new
            {
                aborted = false,
                path = dialog.SelectedPath,
                name = dirInfo.Name
            };
        }

        return new { aborted = true };
    }

    private string GetAppDataFolder(bool create = false)
    {
        var current = rootFolderPath;
        if (string.IsNullOrWhiteSpace(current) || !Directory.Exists(current))
            throw new InvalidOperationException("Chưa chọn thư mục gốc.");
        var dir = Path.Combine(current, AppDataDirName);
        if (create && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        return dir;
    }

    private JsonNode? ReadJsonFileSafe(string filePath, string fallbackJson)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var text = File.ReadAllText(filePath, Encoding.UTF8);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return JsonNode.Parse(text);
                }
            }
        }
        catch
        {
            // Return fallback on parse error
        }
        return JsonNode.Parse(fallbackJson);
    }

    private object HandleLoadAppData()
    {
        var appDataDir = GetAppDataFolder(false);
        var tags = ReadJsonFileSafe(Path.Combine(appDataDir, "tags.json"), "{}");
        var history = ReadJsonFileSafe(Path.Combine(appDataDir, "history.json"), "[]");
        var bookmarks = ReadJsonFileSafe(Path.Combine(appDataDir, "bookmarks.json"), "[]");
        var starredFolders = ReadJsonFileSafe(Path.Combine(appDataDir, "starred-folders.json"), "[]");
        var blacklist = ReadJsonFileSafe(Path.Combine(appDataDir, "blacklist.json"), "[]");
        var likedStories = ReadJsonFileSafe(Path.Combine(appDataDir, "liked-stories.json"), "[]");
        var ratings = ReadJsonFileSafe(Path.Combine(appDataDir, "ratings.json"), "{\"stories\":{},\"authors\":{}}");

        return new
        {
            tags,
            history,
            bookmarks,
            starredFolders,
            blacklist,
            likedStories,
            ratings
        };
    }

    private async Task<object> HandleSaveAppDataFileAsync(JsonNode? @params)
    {
        var fileName = @params?["fileName"]?.GetValue<string>();
        var dataNode = @params?["data"];

        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("Thiếu fileName.");

        var allowedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "tags.json",
            "history.json",
            "bookmarks.json",
            "starred-folders.json",
            "blacklist.json",
            "liked-stories.json",
            "ratings.json"
        };

        if (!allowedFiles.Contains(fileName))
            throw new ArgumentException($"File không hợp lệ: {fileName}");

        await fileWriteLock.WaitAsync();
        try
        {
            var appDataDir = GetAppDataFolder(true);
            var fullPath = Path.Combine(appDataDir, fileName);
            var content = dataNode?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "{}";
            await File.WriteAllTextAsync(fullPath, content, Encoding.UTF8);
            return new { saved = true };
        }
        finally
        {
            fileWriteLock.Release();
        }
    }

    private object HandleGetSelectedBackground()
    {
        LoadSavedSettings();
        return new { selectedBackground };
    }

    private object HandleSaveSelectedBackground(JsonNode? @params)
    {
        var bg = @params?["selectedBackground"]?.GetValue<string>() ?? "";
        selectedBackground = bg;
        SaveSettings();
        return new { saved = true, selectedBackground };
    }

    private List<string> HandleLoadBackgrounds()
    {
        var result = new List<string>();
        try
        {
            var current = rootFolderPath;
            if (string.IsNullOrWhiteSpace(current)) return result;
            var bgDir = Path.Combine(current, AppDataDirName, "BG");
            if (!Directory.Exists(bgDir)) return result;

            var files = new DirectoryInfo(bgDir)
                .EnumerateFiles()
                .Where(f => BgFileRegex.IsMatch(f.Name))
                .OrderBy(f => f.Name, NaturalStringComparer)
                .Select(f => GetRelPath(f.FullName))
                .ToList();

            return files;
        }
        catch
        {
            return result;
        }
    }

    private Dictionary<string, string> HandleLoadAvatars()
    {
        var avatars = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var current = rootFolderPath;
            if (string.IsNullOrWhiteSpace(current)) return avatars;
            var aPicDir = Path.Combine(current, AppDataDirName, "APic");
            if (!Directory.Exists(aPicDir)) return avatars;

            foreach (var file in new DirectoryInfo(aPicDir).EnumerateFiles())
            {
                if (MediaRegex.IsMatch(file.Name))
                {
                    int dotIdx = file.Name.LastIndexOf('.');
                    string authorName = dotIdx > 0 ? file.Name.Substring(0, dotIdx) : file.Name;
                    avatars[authorName] = GetRelPath(file.FullName);
                }
            }
        }
        catch
        {
            // Ignore errors
        }
        return avatars;
    }

    private string GetRelPath(string fullPath)
    {
        var current = rootFolderPath;
        if (string.IsNullOrWhiteSpace(current)) return "";
        var rel = Path.GetRelativePath(current, fullPath).Replace('\\', '/');
        return rel == "." ? "" : rel;
    }

    private object HandleScanDirectory(JsonNode? @params)
    {
        var current = rootFolderPath;
        if (string.IsNullOrWhiteSpace(current) || !Directory.Exists(current))
            throw new InvalidOperationException("Chưa chọn thư mục gốc.");

        var blacklistSet = new HashSet<string>(StringComparer.Ordinal);
        if (@params?["blacklist"] is JsonArray blArr)
        {
            foreach (var item in blArr)
            {
                var val = item?.GetValue<string>();
                if (!string.IsNullOrEmpty(val))
                    blacklistSet.Add(val);
            }
        }

        var invalidStoryFolders = new List<object>();
        var rootDirInfo = new DirectoryInfo(current);
        var rootNode = ScanDirectoryRecursive(rootDirInfo, new List<string>(), blacklistSet, invalidStoryFolders, isRoot: true);

        return new
        {
            rootNode,
            invalidStoryFolders,
            scanTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }

    public sealed class ScanNodeDto
    {
        public string name { get; set; } = "";
        public string type { get; set; } = "folder";
        public string relPath { get; set; } = "";
        public List<string>? path { get; set; }
        public int imageCount { get; set; }
        public string? thumbRelPath { get; set; }
        public bool thumbIsVideo { get; set; }
        public bool isArchive { get; set; }
        public List<ScanNodeDto>? children { get; set; }
    }

    private ScanNodeDto? TryScanArchiveAsStory(FileInfo archiveFile, List<string> parentPath, HashSet<string> usedNames)
    {
        try
        {
            string baseName = Path.GetFileNameWithoutExtension(archiveFile.Name);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = archiveFile.Name;
            string storyName = usedNames.Contains(baseName) ? archiveFile.Name : baseName;

            using var fs = new FileStream(archiveFile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

            var mediaEntries = zip.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name) &&
                            !e.FullName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) &&
                            !e.FullName.Contains("/__MACOSX/", StringComparison.OrdinalIgnoreCase) &&
                            !e.Name.StartsWith('.') &&
                            !e.Name.StartsWith('~') &&
                            MediaRegex.IsMatch(e.Name))
                .Select(e => e.FullName.Replace('\\', '/').TrimStart('/'))
                .OrderBy(n => n, NaturalStringComparer)
                .ToList();

            if (mediaEntries.Count == 0) return null;

            var firstNonVideo = mediaEntries.FirstOrDefault(n => !VideoRegex.IsMatch(n));
            var thumbEntry = firstNonVideo ?? mediaEntries[0];
            var archiveRel = GetRelPath(archiveFile.FullName);

            usedNames.Add(storyName);
            return new ScanNodeDto
            {
                name = storyName,
                type = "story",
                relPath = archiveRel,
                path = parentPath,
                imageCount = mediaEntries.Count,
                thumbRelPath = $"{archiveRel}{ArchiveDelimiter}{thumbEntry}",
                thumbIsVideo = VideoRegex.IsMatch(thumbEntry),
                isArchive = true
            };
        }
        catch
        {
            return null;
        }
    }

    private ScanNodeDto? ScanDirectoryRecursive(
        DirectoryInfo dirInfo,
        List<string> parentPath,
        HashSet<string> blacklistSet,
        List<object> invalidStoryFolders,
        bool isRoot = false)
    {
        if (!isRoot && dirInfo.Name.StartsWith('~'))
        {
            return null;
        }

        var currentFolderPath = new List<string>(parentPath) { dirInfo.Name };
        var currentFolderId = string.Join("/", currentFolderPath);

        if (blacklistSet.Contains(currentFolderId))
        {
            return null;
        }

        var subDirs = new List<DirectoryInfo>();
        var mediaFiles = new List<FileInfo>();
        var archiveFiles = new List<FileInfo>();

        try
        {
            foreach (var entry in dirInfo.EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo subDir)
                {
                    if (isRoot && subDir.Name.Equals(AppDataDirName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (subDir.Name.Equals(ThumbsDirName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (subDir.Name.StartsWith('~'))
                        continue;

                    subDirs.Add(subDir);
                }
                else if (entry is FileInfo file)
                {
                    if (file.Name.StartsWith('~')) continue;
                    if (MediaRegex.IsMatch(file.Name))
                    {
                        mediaFiles.Add(file);
                    }
                    else if (ArchiveRegex.IsMatch(file.Name))
                    {
                        archiveFiles.Add(file);
                    }
                }
            }

            if (mediaFiles.Count > 0 && subDirs.Count == 0 && archiveFiles.Count == 0 && !isRoot)
            {
                mediaFiles.Sort((a, b) => StrCmpLogicalW(a.Name, b.Name));
                var firstNonVideo = mediaFiles.FirstOrDefault(f => !VideoRegex.IsMatch(f.Name));
                var thumbFile = firstNonVideo ?? mediaFiles[0];
                var relPath = GetRelPath(dirInfo.FullName);

                return new ScanNodeDto
                {
                    name = dirInfo.Name,
                    type = "story",
                    relPath = relPath,
                    path = parentPath,
                    imageCount = mediaFiles.Count,
                    thumbRelPath = GetRelPath(thumbFile.FullName),
                    thumbIsVideo = VideoRegex.IsMatch(thumbFile.Name)
                };
            }
            else if (mediaFiles.Count > 0 && archiveFiles.Count == 0 && !isRoot)
            {
                mediaFiles.Sort((a, b) => StrCmpLogicalW(a.Name, b.Name));
                var firstNonVideo = mediaFiles.FirstOrDefault(f => !VideoRegex.IsMatch(f.Name));
                var thumbFile = firstNonVideo ?? mediaFiles[0];
                var relPath = GetRelPath(dirInfo.FullName);

                if (subDirs.Count > 0)
                {
                    invalidStoryFolders.Add(new
                    {
                        name = dirInfo.Name,
                        path = parentPath,
                        relPath,
                        subFolders = subDirs.Select(d => d.Name).ToList()
                    });
                }

                return new ScanNodeDto
                {
                    name = dirInfo.Name,
                    type = "story",
                    relPath = relPath,
                    path = parentPath,
                    imageCount = mediaFiles.Count,
                    thumbRelPath = GetRelPath(thumbFile.FullName),
                    thumbIsVideo = VideoRegex.IsMatch(thumbFile.Name)
                };
            }
            else
            {
                var children = new List<ScanNodeDto>();
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var sub in subDirs)
                {
                    var child = ScanDirectoryRecursive(sub, currentFolderPath, blacklistSet, invalidStoryFolders, isRoot: false);
                    if (child != null && (child.type == "story" || (child.children != null && child.children.Count > 0)))
                    {
                        children.Add(child);
                        usedNames.Add(child.name);
                    }
                }

                archiveFiles.Sort((a, b) => StrCmpLogicalW(a.Name, b.Name));
                foreach (var arc in archiveFiles)
                {
                    var arcStory = TryScanArchiveAsStory(arc, currentFolderPath, usedNames);
                    if (arcStory != null)
                    {
                        children.Add(arcStory);
                    }
                }

                if (children.Count == 0 && !isRoot)
                {
                    return null;
                }

                children.Sort((a, b) => StrCmpLogicalW(a.name, b.name));

                return new ScanNodeDto
                {
                    name = dirInfo.Name,
                    type = "folder",
                    relPath = GetRelPath(dirInfo.FullName),
                    children = children
                };
            }
        }
        catch
        {
            return null;
        }
    }

    private object HandleGetStoryFiles(JsonNode? @params)
    {
        var current = rootFolderPath;
        if (string.IsNullOrWhiteSpace(current))
            throw new InvalidOperationException("Chưa chọn thư mục gốc.");

        var relPath = @params?["relPath"]?.GetValue<string>() ?? "";
        var targetPath = string.IsNullOrEmpty(relPath)
            ? current
            : Path.Combine(current, relPath.Replace('/', Path.DirectorySeparatorChar));

        if (ArchiveRegex.IsMatch(targetPath) && File.Exists(targetPath))
        {
            try
            {
                using var fs = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);
                var archiveFiles = zip.Entries
                    .Where(e => !string.IsNullOrEmpty(e.Name) &&
                                !e.FullName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) &&
                                !e.FullName.Contains("/__MACOSX/", StringComparison.OrdinalIgnoreCase) &&
                                !e.Name.StartsWith('.') &&
                                !e.Name.StartsWith('~') &&
                                MediaRegex.IsMatch(e.Name))
                    .Select(e => e.FullName.Replace('\\', '/').TrimStart('/'))
                    .OrderBy(n => n, NaturalStringComparer)
                    .ToList();
                return new { files = archiveFiles };
            }
            catch
            {
                return new { files = Array.Empty<string>() };
            }
        }

        if (!Directory.Exists(targetPath))
        {
            return new { files = Array.Empty<string>() };
        }

        var files = new DirectoryInfo(targetPath)
            .EnumerateFiles()
            .Where(f => MediaRegex.IsMatch(f.Name))
            .OrderBy(f => f.Name, NaturalStringComparer)
            .Select(f => f.Name)
            .ToList();

        return new { files };
    }

    private object HandleGetTranslateSettings()
    {
        LoadSavedSettings();
        return new
        {
            apiKey = geminiApiKey,
            model = geminiModel,
            targetLang = translateTargetLang,
            customPrompt = translateCustomPrompt
        };
    }

    private object HandleSaveTranslateSettings(JsonNode? @params)
    {
        var apiKey = @params?["apiKey"]?.GetValue<string>()?.Trim() ?? "";
        var model = @params?["model"]?.GetValue<string>()?.Trim();
        var targetLang = @params?["targetLang"]?.GetValue<string>()?.Trim();
        var customPrompt = @params?["customPrompt"]?.GetValue<string>()?.Trim() ?? "";

        geminiApiKey = apiKey;
        if (!string.IsNullOrWhiteSpace(model)) geminiModel = model;
        if (!string.IsNullOrWhiteSpace(targetLang)) translateTargetLang = targetLang;
        translateCustomPrompt = customPrompt;

        SaveSettings();

        return new
        {
            saved = true,
            apiKey = geminiApiKey,
            model = geminiModel,
            targetLang = translateTargetLang,
            customPrompt = translateCustomPrompt
        };
    }

    private async Task<object> HandleTranslateImageAsync(JsonNode? @params)
    {
        var imageBase64 = @params?["imageBase64"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(imageBase64))
            throw new ArgumentException("Không có dữ liệu hình ảnh vùng chọn để dịch.");

        var apiKey = @params?["apiKey"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey)) apiKey = geminiApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("MISSING_API_KEY: Chưa cấu hình Gemini API Key. Vui lòng nhập API Key trong phần Cài đặt dịch AI.");

        var model = @params?["model"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(model)) model = geminiModel;
        if (string.IsNullOrWhiteSpace(model)) model = "gemini-3.5-flash-lite";

        var targetLang = @params?["targetLang"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(targetLang)) targetLang = translateTargetLang;
        if (string.IsNullOrWhiteSpace(targetLang)) targetLang = "Tiếng Việt";

        var customPrompt = @params?["customPrompt"]?.GetValue<string>()?.Trim();
        if (customPrompt == null) customPrompt = translateCustomPrompt;

        var storyName = @params?["storyName"]?.GetValue<string>()?.Trim() ?? "";

        string mimeType = "image/jpeg";
        string base64Data = imageBase64;
        if (base64Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int commaIdx = base64Data.IndexOf(',');
            if (commaIdx > 5)
            {
                string meta = base64Data.Substring(5, commaIdx - 5);
                int semiIdx = meta.IndexOf(';');
                if (semiIdx > 0)
                {
                    mimeType = meta.Substring(0, semiIdx);
                }
                base64Data = base64Data.Substring(commaIdx + 1);
            }
        }

        var systemPromptBuilder = new StringBuilder();
        systemPromptBuilder.AppendLine("Bạn là chuyên gia OCR và biên dịch truyện tranh (Manga, Manhwa, Manhua, Comic).");
        systemPromptBuilder.AppendLine("Nhiệm vụ:");
        systemPromptBuilder.AppendLine("1. Nhận diện chính xác toàn bộ chữ trong vùng ảnh được cắt (kể cả chữ dọc, chữ viết tay, hiệu ứng âm thanh SFX, font chữ cách điệu).");
        systemPromptBuilder.AppendLine("2. Nếu có nhiều ô thoại hoặc nhiều câu tách biệt, hãy sắp xếp theo đúng thứ tự đọc truyện tự nhiên và xuống dòng rõ ràng giữa các câu.");
        systemPromptBuilder.AppendLine($"3. Dịch toàn bộ nội dung sang {targetLang} sao cho tự nhiên, mượt mà, đúng ngữ cảnh hội thoại và cảm xúc nhân vật.");
        if (!string.IsNullOrWhiteSpace(storyName))
        {
            systemPromptBuilder.AppendLine($"Ngữ cảnh tên truyện đang đọc: {storyName}");
        }
        if (!string.IsNullOrWhiteSpace(customPrompt))
        {
            systemPromptBuilder.AppendLine($"Yêu cầu bổ sung từ người đọc: {customPrompt}");
        }
        systemPromptBuilder.AppendLine("Bắt buộc chỉ trả về duy nhất một đối tượng JSON hợp lệ có cấu trúc:");
        systemPromptBuilder.AppendLine("{\"original\": \"<văn bản gốc trích xuất từ ảnh>\", \"translated\": \"<bản dịch>\"}");
        systemPromptBuilder.AppendLine("Nếu trong ảnh hoàn toàn không có chữ nào, trả về: {\"original\": \"\", \"translated\": \"(Không phát hiện thấy chữ trong vùng đã chọn)\"}");

        var payloadObj = new
        {
            system_instruction = new
            {
                parts = new[] { new { text = systemPromptBuilder.ToString() } }
            },
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = $"Hãy đọc chữ trong ảnh này và dịch sang {targetLang}. Trả về JSON với 2 trường original và translated." },
                        new
                        {
                            inline_data = new
                            {
                                mime_type = mimeType,
                                data = base64Data
                            }
                        }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0.2,
                responseMimeType = "application/json"
            }
        };

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent?key={Uri.EscapeDataString(apiKey)}";
        var jsonPayload = JsonSerializer.Serialize(payloadObj);

        using var reqContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
        using var resp = await httpClient.PostAsync(url, reqContent);
        var respBody = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            string apiErrMsg = $"Lỗi Gemini API (HTTP {(int)resp.StatusCode})";
            try
            {
                var errNode = JsonNode.Parse(respBody);
                var msgText = errNode?["error"]?["message"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(msgText))
                {
                    apiErrMsg += $": {msgText}";
                }
            }
            catch
            {
                if (!string.IsNullOrWhiteSpace(respBody) && respBody.Length < 300)
                    apiErrMsg += $": {respBody}";
            }
            throw new InvalidOperationException(apiErrMsg);
        }

        string rawText = "";
        try
        {
            var rootNode = JsonNode.Parse(respBody);
            var partsArray = rootNode?["candidates"]?[0]?["content"]?["parts"] as JsonArray;
            if (partsArray != null)
            {
                var sb = new StringBuilder();
                foreach (var part in partsArray)
                {
                    bool isThought = part?["thought"]?.GetValue<bool>() == true;
                    if (isThought) continue;
                    var t = part?["text"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(t))
                    {
                        sb.Append(t);
                    }
                }
                rawText = sb.ToString().Trim();
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Không thể đọc phản hồi từ Gemini API: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new
            {
                original = "",
                translated = "(Không nhận được kết quả dịch từ AI)",
                model
            };
        }

        string cleanedJson = rawText;
        if (cleanedJson.StartsWith("```"))
        {
            int firstNewLine = cleanedJson.IndexOf('\n');
            if (firstNewLine > 0)
            {
                cleanedJson = cleanedJson.Substring(firstNewLine + 1);
            }
            if (cleanedJson.EndsWith("```"))
            {
                cleanedJson = cleanedJson.Substring(0, cleanedJson.Length - 3);
            }
            cleanedJson = cleanedJson.Trim();
        }

        string originalText = "";
        string translatedText = rawText;
        try
        {
            var parsed = JsonNode.Parse(cleanedJson);
            if (parsed is JsonObject objNode)
            {
                originalText = objNode["original"]?.GetValue<string>() ?? "";
                translatedText = objNode["translated"]?.GetValue<string>() ?? rawText;
            }
        }
        catch
        {
            // Fallback to rawText as translatedText if JSON parsing fails
        }

        return new
        {
            original = originalText,
            translated = translatedText,
            model
        };
    }
}
