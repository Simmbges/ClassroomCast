# 教室屏幕共享（局域网屏幕直播）

老师端将屏幕实时共享给同一局域网内的多个学生观看，学生可同时正常操作自己的电脑（看一眼、跟着练）。
纯局域网工具：**不锁屏、不远程控制、不传声音、不录像、不连互联网、不需要账号**。

- 老师端 **Server**：自动列出本机局域网 IPv4（含网卡名，可辨认选择）、选择帧率（5/10/15/30/60 FPS，默认 15，运行中可切换）、开始/停止共享、实时显示在线学生名单与人数、显示实际编码帧率。
- 学生端 **Client**：输入姓名与老师 IP 即可观看；观看窗口可自由调整大小（保持宽高比）、可最小化、可与其他软件并排使用；同名学生自动编号（张三、张三(2)）。
- 技术栈：C# / .NET 8 WinForms；GDI 屏幕采集 + JPEG 编码（一帧编码一次，全体复用）；TCP 自定义二进制协议（MJPEG 思路，每帧独立 JPEG）；每学生独立发送队列，慢客户端丢帧不拖累他人。

## 目录结构

```
├── src/ScreenShare/            程序源码
│   ├── Core/                   协议、地址枚举、采集编码、服务端、客户端
│   └── UI/                     主窗体（Server/Client 双页）、学生观看窗口
├── tests/SmokeTest/            零依赖冒烟测试（含 --client 手动连接模式）
├── scripts/
│   ├── publish.ps1             一键打包（两种发布形态）
│   ├── firewall.ps1            老师端防火墙放行（管理员运行一次）
│   ├── screenshot.ps1 / click-button.ps1 / inspect-ui.ps1 / client-gui-test.ps1
│   │                           开发期 GUI 自动化辅助（交付测试用，可删）
├── publish/                    打包产物
│   ├── 教室屏幕共享-免安装版/ClassroomScreenShare.exe      (~60 MB，目标机零依赖)
│   └── 教室屏幕共享-需运行时版/ClassroomScreenShare.exe    (~0.2 MB，需 .NET 8 桌面运行时)
└── docs/
    ├── 使用说明.md             老师/学生操作步骤、防火墙、常见问题
    ├── 技术方案与架构.md       选型理由、协议、性能限制、改进建议
    └── 测试报告.md             已执行的测试与结果、未验证项
```

## 环境要求

- 运行：Windows 10 1903 及以上 / Windows 11（x64）
- 免安装版无需任何运行时；需运行时版要安装 [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0)
- 构建：.NET 8 SDK（`winget install Microsoft.DotNet.SDK.8`，或用 dotnet-install 脚本装到用户目录）

## 构建与运行

```powershell
# 构建（调试运行）
dotnet run --project src/ScreenShare -c Release

# 运行冒烟测试（协议/端到端/性能实测，全绿为通过）
dotnet run --project tests/SmokeTest -c Release

# 手动连接验证（老师端共享中时）
dotnet run --project tests/SmokeTest -c Release -- --client <老师IP> 9527 学生甲

# 打包两种发布形态
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1
```

## 快速上手

1. 老师电脑运行 `ClassroomScreenShare.exe` → 选 **Server 老师端** 页 → 确认本机地址与端口（默认 9527）→ 点 **Server - 开始共享**。
   首次共享时 Windows 防火墙会弹窗，勾选"专用网络"并允许；或提前以管理员运行 `scripts/firewall.ps1` 放行端口。
2. 学生电脑运行同一程序 → 选 **Client 学生端** 页 → 输入姓名与老师显示的 IP → 点 **Client - 连接**，自动弹出观看窗口。

详细步骤、防火墙配置与排障见 [docs/使用说明.md](docs/使用说明.md)。

## 许可与依赖

本项目代码无第三方 NuGet 依赖，仅使用 .NET 8 内置库（System.Windows.Forms、System.Drawing、System.Net 等，遵循 .NET MIT 许可）。打包产物中 self-contained 版本包含 .NET 8 运行时（MIT 许可），除此之外不含任何第三方组件。
