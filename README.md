# Truyện của toi (rdtruyen)

Ứng dụng đọc truyện tranh / manga & xem media trên Windows (WinForms + WebView2), tích hợp máy chủ LAN nội bộ để đọc trực tiếp trên điện thoại qua Wi-Fi và hỗ trợ dịch trang truyện bằng Gemini AI.

## Tính năng chính
- **Quản lý & đọc truyện trên PC**: Hỗ trợ thư mục ảnh, file nén (`.zip`, `.cbz`), video (`.mp4`, `.webm`), tự động tạo ảnh thu nhỏ (`.thumbs`), đánh dấu yêu thích, lịch sử đọc và gắn thẻ (tags).
- **Đọc trên điện thoại qua mạng LAN (Wi-Fi)**: Tích hợp máy chủ HTTP nội bộ với giao diện tối ưu riêng cho Mobile (`index-mobile.html`), hỗ trợ bảo vệ bằng mật khẩu (PBKDF2) và quét mã QR để kết nối nhanh.
- **Dịch truyện bằng AI (Gemini)**: Khoanh vùng hoặc dịch trực tiếp trang truyện sang Tiếng Việt (hoặc ngôn ngữ tùy chọn) thông qua Gemini API.
- **Khay hệ thống (System Tray) & Phím tắt ẩn nhanh**: Hỗ trợ thu nhỏ xuống khay hệ thống bằng phím tắt toàn cục (mặc định `Alt+X`, có thể tùy chỉnh).

## Yêu cầu hệ thống
- Hệ điều hành: Windows 10/11 (64-bit)
- [.NET 9.0 SDK / Windows Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)
- Microsoft Edge WebView2 Runtime (đã có sẵn trên Windows 10/11 hiện đại)

## Hướng dẫn chạy và đóng gói (Build)

### 1. Chạy trực tiếp ở chế độ phát triển
```bash
dotnet run
```

### 2. Xuất bản (Publish) ra file chạy `.exe`
```bash
dotnet publish -c Release -r win-x64 --self-contained false -o publish
```
File chạy sau khi xuất bản sẽ nằm tại `publish/TruyenCuaToi.exe`.

## Lưu trữ cấu hình
Cấu hình cá nhân (thư mục gốc, khóa API Gemini, mật khẩu LAN) được lưu tự động và tách biệt tại:
- `%LocalAppData%\TruyenCuaToi\app-settings.json`
- `%LocalAppData%\TruyenCuaToi\mobile-auth.json`
