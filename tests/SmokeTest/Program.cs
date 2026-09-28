using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ScreenShare.Core;
using ScreenShare.UI;

namespace SmokeTest;

/// <summary>
/// 无外部依赖的回环冒烟测试：真实执行网络 IO、屏幕采集与 JPEG 编码。
/// 输出每项 PASS/FAIL，全部通过返回退出码 0。
/// 用法：dotnet run --project tests/SmokeTest [-ea 列表]  [-ea all]
/// </summary>
internal static class Program
{
    private static int _failures;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // --client <ip> <port> [姓名]：连接指定服务器接收 10 秒画面并输出统计
        if (args.Length >= 2 && args[0] == "--client")
            return await RunAsClient(args[1], int.Parse(args[2]), args.Length > 3 ? args[3] : "测试学生");

        // --dpi：DPI 缩放行为探针
        if (args.Length >= 1 && args[0] == "--dpi")
        {
            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
            return DpiProbe();
        }

        Console.WriteLine("=== 教室屏幕共享 冒烟测试 ===");
        Console.WriteLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"系统: {Environment.OSVersion.VersionString}, {Environment.ProcessorCount} 逻辑核");
        Console.WriteLine();

        // 无需屏幕/网络的纯逻辑
        await Run("协议消息读写往返", ProtocolRoundTrip);
        await Run("非法消息长度被拒绝", ProtocolRejectsBadLength);
        await Run("局域网 IPv4 地址枚举", AddressEnum);

        // 网络回环端到端
        await Run("端口被占用时启动报错", PortInUse);
        await Run("连接失败给出中文错误", ConnectFailureChinese);
        await Run("端到端：双客户端/同名/掉线/停止恢复/慢客户端", EndToEnd);
        await Run("屏幕采集 + JPEG 编码性能实测", PerfCaptureEncode);

        // GUI 冒烟（STA）
        await Run("GUI 主窗体构造与地址填充", GuiSmoke);

        Console.WriteLine();
        if (_failures == 0)
        {
            Console.WriteLine("全部通过 ✔");
            return 0;
        }
        Console.WriteLine($"失败 {_failures} 项 ✘");
        return 1;
    }

    private static async Task Run(string name, Func<Task> test)
    {
        Console.Write($"[{name}] … ");
        try
        {
            await test();
            Console.WriteLine("PASS");
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine("FAIL");
            Console.WriteLine($"    {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Task Run(string name, Action test) => Run(name, () => { test(); return Task.CompletedTask; });

    private static void Check(bool cond, string message)
    {
        if (!cond) throw new Exception(message);
    }

    /// <summary>DPI 探针：验证 AutoScaleMode.Dpi 在本机是否真正缩放控件尺寸。</summary>
    private static int DpiProbe()
    {
        Exception? err = null;
        string report = "";
        var t = new Thread(() =>
        {
            try
            {
                // 验证：句柄创建前 DeviceDpi 是否已是真实屏幕 DPI（决定手动缩放因子是否可靠）
                var f = new Form { Text = "probe", Font = new Font("Microsoft YaHei UI", 9F) };
                var b = new Button { Left = 20, Top = 20, Width = 100, Height = 38, Text = "测试按钮" };
                report = $"[构造期] form.DeviceDpi={f.DeviceDpi}, btn.DeviceDpi={b.DeviceDpi}, font.Height={f.Font.Height}";
            }
            catch (Exception ex) { err = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Console.WriteLine(report);
        if (err != null) Console.WriteLine("异常: " + err);
        return 0;
    }

    /// <summary>命令行客户端模式：连接正在运行的老师端，验证真实画面传输。</summary>
    private static async Task<int> RunAsClient(string ip, int port, string name)
    {
        var c = new StreamClient();
        int frames = 0;
        c.FrameDecoded += _ => Interlocked.Increment(ref frames);
        c.StatusChanged += s => Console.WriteLine($"[状态] {s}");
        c.ConnectionLost += s => Console.WriteLine($"[断开] {s}");
        try
        {
            await c.ConnectAsync(ip, port, name, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine("连接失败：" + ex.Message);
            return 2;
        }
        Console.WriteLine($"已连接 {ip}:{port}（姓名：{name}），接收 10 秒画面…");
        await Task.Delay(10_000);
        int f = Volatile.Read(ref frames);
        Console.WriteLine($"10 秒共收到 {f} 帧（约 {f / 10.0:0.0} FPS）");
        c.Disconnect();
        return f > 0 ? 0 : 1;
    }

    private static async Task WaitUntil(Func<bool> cond, string what, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new Exception($"等待超时：{what}");
            await Task.Delay(50);
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // ---------- 测试用 StreamClient 包装：收帧计数 ----------
    private sealed class TestClient
    {
        public readonly StreamClient Client = new();
        public int Frames;
        public string? LastStatus;
        public bool StopNotified;

        public TestClient ConnectEvents()
        {
            Client.FrameDecoded += _ => Interlocked.Increment(ref Frames);
            Client.StatusChanged += s => { LastStatus = s; if (s.Contains("已停止共享")) StopNotified = true; };
            return this;
        }

        public Task Connect(string ip, int port, string name) =>
            Client.ConnectAsync(ip, port, name, CancellationToken.None);
    }

    // ---------- 1. 协议 ----------
    private static async Task ProtocolRoundTrip()
    {
        using var pipe = new MemoryStream();
        var payload = Encoding.UTF8.GetBytes("测试数据 123");
        var msg = Protocol.BuildMessage(Protocol.Frame, payload);

        // 用一对互联的匿名管道验证完整读写
        using var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        server.Listen(1);
        var client = new TcpClient();
        var accept = server.AcceptAsync();
        await client.ConnectAsync(((IPEndPoint)server.LocalEndPoint!).Address, ((IPEndPoint)server.LocalEndPoint!).Port);
        var serverSide = await accept;
        await using var ns = new NetworkStream(serverSide);
        await using var cs = client.GetStream();

        await Protocol.WriteAsync(cs, Protocol.Frame, msg, CancellationToken.None);
        var (type, received) = await Protocol.ReadMessageAsync(ns, CancellationToken.None);
        Check(type == Protocol.Frame, "消息类型不一致");
        Check(received.AsSpan().SequenceEqual(msg), "载荷内容不一致");
        Check(received.Length == msg.Length, "载荷长度不一致");
    }

    private static async Task ProtocolRejectsBadLength()
    {
        var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        server.Listen(1);
        var client = new TcpClient();
        var accept = server.AcceptAsync();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)server.LocalEndPoint!).Port);
        var serverSide = await accept;
        await using var ns = new NetworkStream(serverSide);
        await using var cs = client.GetStream();

        // 头部声明 2GB 长度
        var bad = new byte[] { Protocol.Frame, 0xFF, 0xFF, 0xFF, 0x7F };
        await cs.WriteAsync(bad);
        try
        {
            await Protocol.ReadMessageAsync(ns, CancellationToken.None);
            throw new Exception("非法长度未被拒绝");
        }
        catch (InvalidDataException) { /* 预期 */ }
    }

    // ---------- 2. 地址枚举 ----------
    private static void AddressEnum()
    {
        var list = NetworkUtils.GetLocalIPv4Addresses();
        Console.Write($"\n    发现 {list.Count} 个可用地址: ");
        Console.WriteLine(string.Join(", ", list.Select(a => a.Address)));
        foreach (var a in list)
        {
            Check(!IPAddress.IsLoopback(a.Address), "不应包含回环地址");
            Check(!a.Address.ToString().StartsWith("169.254."), "不应包含自动配置地址");
        }
    }

    // ---------- 3. 端口占用 ----------
    private static async Task PortInUse()
    {
        int port = FreePort();
        var s1 = new StreamServer();
        s1.Start("127.0.0.1", port); // 正常启动并占用端口
        try
        {
            var s2 = new StreamServer();
            try
            {
                s2.Start("127.0.0.1", port);
                s2.Stop();
                throw new Exception("端口被占用时未报错");
            }
            catch (SocketException se)
            {
                Check(se.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied,
                    $"异常类型应为 AddressAlreadyInUse，实际 {se.SocketErrorCode}");
            }
        }
        finally { s1.Stop(); }
        await Task.CompletedTask;
    }

    // ---------- 4. 连接失败中文提示 ----------
    private static async Task ConnectFailureChinese()
    {
        // 无效 IP 格式
        var c = new StreamClient();
        try
        {
            await c.ConnectAsync("999.1.1.1", 12345, "小明", CancellationToken.None);
            throw new Exception("无效 IP 未被拒绝");
        }
        catch (ArgumentException ex)
        {
            Check(ex.Message.Contains("IP 地址格式不正确"), $"提示应说明 IP 格式错误，实际：{ex.Message}");
        }

        // 端口无人监听 → 连接拒绝
        int free = FreePort();
        var c2 = new StreamClient();
        try
        {
            await c2.ConnectAsync("127.0.0.1", free, "小明", CancellationToken.None);
            throw new Exception("连接无监听端口未报错");
        }
        catch (InvalidOperationException ex)
        {
            Check(ex.Message.Contains("无法连接到老师电脑"), $"提示应面向学生，实际：{ex.Message}");
        }
    }

    // ---------- 5. 端到端 ----------
    private static async Task EndToEnd()
    {
        int port = FreePort();
        var server = new StreamServer();
        var logs = new List<string>();
        server.Log += m => { lock (logs) logs.Add(m); };
        server.Start("127.0.0.1", port);
        server.StartStreaming(15);

        // 客户端 1：小明
        var c1 = new TestClient().ConnectEvents();
        await c1.Connect("127.0.0.1", port, "小明");
        await WaitUntil(() => Volatile.Read(ref c1.Frames) >= 5, "客户端1 收到前5帧", 15_000);
        Check(c1.Client.IsStreaming, "客户端1 应处于直播状态");

        // 客户端 2：小红（第二个并发客户端）
        var c2 = new TestClient().ConnectEvents();
        await c2.Connect("127.0.0.1", port, "小红");
        await WaitUntil(() => Volatile.Read(ref c2.Frames) >= 3, "客户端2 收到帧", 15_000);
        Check(c2.Client.IsStreaming, "客户端2 应处于直播状态");

        // 同名学生：两个张三
        var c3 = new TestClient().ConnectEvents();
        var c4 = new TestClient().ConnectEvents();
        await c3.Connect("127.0.0.1", port, "张三");
        await c4.Connect("127.0.0.1", port, "张三");
        await WaitUntil(() => server.ClientCount == 4, "服务端名单应有 4 人", 10_000);
        var names = server.GetClientDetails().Select(d => d.DisplayName).ToList();
        Check(names.Contains("张三") && names.Contains("张三(2)"), $"同名应编号显示，实际：[{string.Join("、", names)}]");
        Check(names.Contains("小明") && names.Contains("小红"), $"名单应包含小明小红，实际：[{string.Join("、", names)}]");

        // 学生主动断开
        c2.Client.Disconnect();
        await WaitUntil(() => server.ClientCount == 3, "断开后名单应剩 3 人", 10_000);

        // 慢客户端：原生连接后不读数据（模拟网络缓慢）
        int framesBeforeSlow = Volatile.Read(ref c1.Frames);
        var slowRaw = new TcpClient();
        await slowRaw.ConnectAsync(IPAddress.Loopback, port);
        var hello = new byte[] { Protocol.Version };
        hello = hello.Concat(Encoding.UTF8.GetBytes("慢速同学")).ToArray();
        var helloMsg = Protocol.BuildMessage(Protocol.ClientHello, hello);
        await slowRaw.GetStream().WriteAsync(helloMsg);
        // 不读取任何数据，让服务端发送缓冲逐渐填满
        await Task.Delay(5000);
        int slowWindow = Volatile.Read(ref c1.Frames) - framesBeforeSlow;
        Check(slowWindow >= 30, $"慢客户端阻塞期间，正常客户端 5 秒内应继续收到帧（期望≥30，实际 {slowWindow}）");
        slowRaw.Close();

        // 老师停止共享 → 学生收到明确提示
        server.StopStreaming();
        await WaitUntil(() => c1.StopNotified && c3.StopNotified && c4.StopNotified, "学生应收到停止共享提示", 10_000);
        Check(!c1.Client.IsStreaming, "客户端1 直播状态应为否");

        // 再次开始共享 → 恢复收帧（无需重连）
        int framesAtResume = Volatile.Read(ref c1.Frames);
        server.StartStreaming(5); // 换低帧率验证运行中切换
        await WaitUntil(() => Volatile.Read(ref c1.Frames) >= framesAtResume + 3, "恢复共享后客户端1 继续收到帧", 15_000);

        // 高帧率切换仍有效
        framesAtResume = Volatile.Read(ref c1.Frames);
        server.StartStreaming(30);
        await Task.Delay(3000);
        int highWindow = Volatile.Read(ref c1.Frames) - framesAtResume;
        Check(highWindow >= 40, $"切换 30FPS 后 3 秒应收帧明显增多（期望≥40，实际 {highWindow}）");

        // 服务端完全停止 → 名单清空
        server.Stop();
        await WaitUntil(() => server.ClientCount == 0, "服务停止后名单应清空", 10_000);
        await WaitUntil(() => !c1.Client.IsConnected, "客户端1 应感知断开", 10_000);

        c1.Client.Disconnect();
        c3.Client.Disconnect();
        c4.Client.Disconnect();

        lock (logs) Console.WriteLine("    服务端日志: " + string.Join(" | ", logs.TakeLast(4)));
    }

    // ---------- 6. 性能实测 ----------
    private static void PerfCaptureEncode()
    {
        using var capture = new ScreenCapture();
        Console.Write($"\n    采集区域: {capture.Width}x{capture.Height}");

        // 预热
        for (int i = 0; i < 3; i++)
            JpegEncoder.Encode(capture.Capture(), StreamServer.JpegQuality);

        const int frames = 30;
        var sw = Stopwatch.StartNew();
        long totalBytes = 0;
        long maxMs = 0;
        for (int i = 0; i < frames; i++)
        {
            var fsw = Stopwatch.StartNew();
            var jpeg = JpegEncoder.Encode(capture.Capture(), StreamServer.JpegQuality);
            fsw.Stop();
            totalBytes += jpeg.Length;
            maxMs = Math.Max(maxMs, fsw.ElapsedMilliseconds);
            Check(jpeg.Length > 2 && jpeg[0] == 0xFF && jpeg[1] == 0xD8, "输出应为 JPEG（FFD8 开头）");
        }
        sw.Stop();

        double avgMs = sw.Elapsed.TotalMilliseconds / frames;
        double avgKb = totalBytes / 1024.0 / frames;
        Console.WriteLine($", 平均单帧(采集+编码) {avgMs:0.0} ms, 单帧最大 {maxMs} ms, " +
                          $"平均 {avgKb:0} KB/帧, 单客户端场景估算可支撑 {1000.0 / avgMs:0} FPS");
    }

    // ---------- 7. GUI 冒烟 ----------
    private static void GuiSmoke()
    {
        // ServerPanel/ClientPanel 构造需 STA（WinForms 控件）
        Exception? panelError = null;
        int addressCount = -1;
        var t = new Thread(() =>
        {
            try
            {
                using var panel = new ServerPanel();
                addressCount = panel.AddressCount;
                using var clientPanel = new ClientPanel();
            }
            catch (Exception ex) { panelError = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Check(panelError is null, $"面板构造异常: {panelError?.Message}");
        Check(addressCount != 0, $"地址枚举结果异常（{addressCount} 个）");
        Console.Write($"（枚举到 {addressCount} 个地址）");
    }
}
