# 教室屏幕共享（局域网屏幕直播）

老师端将屏幕实时共享给同一局域网内的多个学生观看，学生可同时正常操作自己的电脑（看一眼、跟着练）。
纯局域网工具：**不锁屏、不远程控制、不传声音、不录像、不连互联网、不需要账号**。

- 老师端 **Server**：自动列出本机局域网 IPv4（含网卡名，可辨认选择）、选择帧率（5/10/15/30/60 FPS，默认 15，运行中可切换）、开始/停止共享、实时显示在线学生名单与人数、显示实际编码帧率。
- 学生端 **Client**：输入姓名与老师 IP 即可观看；观看窗口可自由调整大小（保持宽高比）、可最小化、可与其他软件并排使用；同名学生自动编号（张三、张三(2)）。
- 共享画面包含老师的鼠标指针。
- 每轮课堂签到自动保存到老师电脑的 `%LOCALAPPDATA%\教室屏幕共享\签到记录`；老师端可直接打开历史记录目录。
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
├── .gitignore                  忽略生成文件与本机数据
├── LICENSE                     项目源码的 MIT 许可证
├── publish/                    本地打包产物（运行打包脚本生成，不提交到仓库）
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

## 发布到代码仓库

仓库只保存源码、测试、脚本、文档和许可证；`bin/`、`obj/`、`publish/` 已由 `.gitignore` 排除，下载源码后按上面的命令重新构建即可。

可运行程序包作为发行版附件单独上传。免安装版应压缩整个 `publish/教室屏幕共享-免安装版` 目录，保留其中的 DLL 文件；不要只复制 EXE。分发时附上 `LICENSE`、`docs/使用说明.md` 和 `scripts/firewall.ps1`。不要上传本地签到记录、导出名单或开发截图。

## 快速上手

1. 老师电脑运行 `ClassroomScreenShare.exe` → 选 **Server 老师端** 页 → 确认本机地址与端口（默认 9527）→ 点 **Server - 开始共享**。
   首次共享时 Windows 防火墙会弹窗，勾选"专用网络"并允许；或提前以管理员运行 `scripts/firewall.ps1` 放行端口。
2. 学生电脑运行同一程序 → 选 **Client 学生端** 页 → 输入姓名与老师显示的 IP → 点 **Client - 连接**，自动弹出观看窗口。

详细步骤、防火墙配置与排障见 [docs/使用说明.md](docs/使用说明.md)。

## 许可与依赖

本项目源码采用 [MIT 许可证](LICENSE)。项目没有外部 NuGet 包依赖，使用 .NET 8 自带 API（System.Windows.Forms、System.Drawing、System.Net 等）。免安装版还包含 .NET 8 桌面运行时组件，其许可与本项目源码的许可分别适用。
