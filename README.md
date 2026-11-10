# Truyện của toi (RDTruyen)

Ứng dụng đọc truyện trên Windows, tích hợp máy chủ LAN nội bộ để đọc trực tiếp trên điện thoại qua Wi-Fi và hỗ trợ dịch trang truyện bằng AI qua API.

## Tính năng chính
- **Quản lý & đọc truyện trên PC**: Hỗ trợ thư mục ảnh, file nén, video, đánh dấu yêu thích, lịch sử đọc và gắn thẻ (tags).
- **Đọc trên điện thoại qua mạng LAN**: Tích hợp máy chủ HTTP nội bộ với giao diện riêng cho Mobile, hỗ trợ mật khẩu (PBKDF2) và quét mã QR để kết nối nhanh.
- **Dịch truyện bằng AI**: Khoanh vùng hoặc dịch trực tiếp trang truyện sang Tiếng Việt hoặc ngôn ngữ tùy chọn thông qua Gemini API.

## Yêu cầu hệ thống
- Hệ điều hành: Windows 10/11 (64-bit)
- [.NET 9.0 SDK / Windows Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)
- Microsoft Edge WebView2 Runtime (đã có sẵn trên Windows 10/11 hiện đại)

## Hướng dẫn chạy và đóng gói (Build)

### 1. Chạy trực tiếp ở chế độ phát triển
```bash
dotnet run
```

### 2. Xuất bản ra file chạy `.exe`
```bash
dotnet publish -c Release -r win-x64 --self-contained false -o publish
```
File chạy sau khi xuất bản sẽ nằm tại `publish/TruyenCuaToi.exe`.

## Lưu trữ cấu hình
Cấu hình cá nhân (thư mục gốc, khóa API Gemini, mật khẩu LAN) được lưu tự động và tách biệt tại:
- `%LocalAppData%\TruyenCuaToi\app-settings.json`
- `%LocalAppData%\TruyenCuaToi\mobile-auth.json`
