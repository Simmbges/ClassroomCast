using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
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
        await Run("控制消息不会被画面帧挤掉", ControlMessagesSurviveFrames);
        await Run("断连与画面入队并发不会抛异常", ConcurrentDisconnectAndFrames);
        await Run("局域网 IPv4 地址枚举", AddressEnum);

        // 网络回环端到端
        await Run("端口被占用时启动报错", PortInUse);
        await Run("连接失败给出中文错误", ConnectFailureChinese);
        await Run("端到端：双客户端/同名/掉线/停止恢复/慢客户端", EndToEnd);
        await Run("端到端：定时签到", SignInE2E);
        await Run("60 名学生同时接入", ConnectionBurst);
        await Run("界面忙时仅保留最新画面", ViewerKeepsLatestFrame);
        await Run("屏幕采集 + JPEG 编码性能实测", PerfCaptureEncode);
        await Run("屏幕采集包含鼠标指针", CursorCaptured);

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

    private static void ControlMessagesSurviveFrames()
    {
        using var tcp = new TcpClient();
        var session = new ClientSession(1, "测试学生", tcp);
        session.Enqueue(Protocol.BuildMessage(Protocol.SignInStart, BitConverter.GetBytes(60)));
        for (int i = 0; i < 100; i++)
            session.Enqueue(Protocol.BuildMessage(Protocol.Frame, [(byte)i]));
        session.Enqueue(Protocol.BuildMessage(Protocol.SignInResult, [0]));

        var take = typeof(ClientSession).GetMethod("TakeNextMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = (byte[])take.Invoke(session, null)!;
        var second = (byte[])take.Invoke(session, null)!;
        var latest = (byte[])take.Invoke(session, null)!;
        Check(first[0] == Protocol.SignInStart, "签到开始消息被画面覆盖");
        Check(second[0] == Protocol.SignInResult, "签到结果消息被画面覆盖");
        Check(latest[0] == Protocol.Frame && latest[5] == 99, "画面没有保留最新一帧");
        session.MarkDisconnected();
    }

    private static void ConcurrentDisconnectAndFrames()
    {
        for (int i = 0; i < 2000; i++)
        {
            using var tcp = new TcpClient();
            var session = new ClientSession(i, "测试学生", tcp);
            Parallel.Invoke(
                () => { for (int j = 0; j < 8; j++) session.Enqueue(Protocol.BuildMessage(Protocol.Frame, [1])); },
                session.MarkDisconnected);
            Check(!session.Enqueue(Protocol.BuildMessage(Protocol.Frame, [1])), "已断开连接仍可入队");
        }
    }

    private static async Task ConnectionBurst()
    {
        int port = FreePort();
        var server = new StreamServer();
        var clients = new List<TcpClient>();
        server.Start("127.0.0.1", port);
        var sw = Stopwatch.StartNew();
        try
        {
            for (int i = 0; i < 60; i++)
            {
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                clients.Add(client);
                var hello = new byte[] { Protocol.Version }
                    .Concat(Encoding.UTF8.GetBytes($"学生{i}"))
                    .ToArray();
                await client.GetStream().WriteAsync(Protocol.BuildMessage(Protocol.ClientHello, hello));
            }
            await WaitUntil(() => server.ClientCount == 60, "60 人应及时进入在线名单", 5000);
            Console.Write($"（全部接入耗时 {sw.ElapsedMilliseconds} ms）");
        }
        finally
        {
            server.Stop();
            foreach (var client in clients) client.Dispose();
        }
    }

    private static void ViewerKeepsLatestFrame()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var viewer = new ViewerForm("测试学生");
                _ = viewer.Handle;
                Bitmap? latest = null;
                for (int i = 0; i < 200; i++)
                {
                    latest = new Bitmap(4, 4);
                    latest.SetPixel(0, 0, Color.FromArgb(i, 0, 0));
                    viewer.QueueFrame(latest);
                }
                var pending = (Bitmap?)typeof(ViewerForm).GetField("_pendingFrame", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer);
                Check(ReferenceEquals(latest, pending), "界面队列没有保留最新画面");
                typeof(ViewerForm).GetMethod("ShowPendingFrame", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewer, null);
                var box = (PictureBox)typeof(ViewerForm).GetField("_box", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer)!;
                Check(ReferenceEquals(box.Image, latest), "界面没有显示最新画面");
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Check(error is null, $"观看窗口画面队列异常：{error}");
    }

    private static void CursorCaptured()
    {
        var bounds = SystemInformation.VirtualScreen;
        var cursor = Cursor.Position;
        Check(bounds.Contains(cursor), "鼠标当前不在被采集的桌面内");
        using var baseline = new Bitmap(bounds.Width, bounds.Height);
        using (var graphics = Graphics.FromImage(baseline))
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        using var capture = new ScreenCapture();
        var frame = capture.Capture();
        var x = cursor.X - bounds.X;
        var y = cursor.Y - bounds.Y;
        int differences = 0;
        for (int py = Math.Max(0, y - 32); py < Math.Min(bounds.Height, y + 32); py++)
            for (int px = Math.Max(0, x - 32); px < Math.Min(bounds.Width, x + 32); px++)
                if (baseline.GetPixel(px, py) != frame.GetPixel(px, py)) differences++;
        Check(differences > 0, "采集画面在鼠标附近与不含指针的屏幕复制完全相同");
        Console.Write($"（鼠标附近有 {differences} 个不同像素）");
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
        c.SignInStarted += s => Console.WriteLine($"[签到] 老师发起了签到，剩余 {s} 秒");
        c.SignInEnded += () => Console.WriteLine("[签到] 签到已结束");
        c.SignInResultReceived += (ok, r) => Console.WriteLine($"[签到] 提交{(ok ? "成功" : "失败")}：{r}");
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

        // 签到事件
        public volatile int SignInStartedSeconds = -1;
        public volatile bool SignInEndedReceived;
        public volatile bool SignInSuccess;
        public volatile string? SignInResultText;

        public TestClient ConnectEvents()
        {
            Client.FrameDecoded += _ => Interlocked.Increment(ref Frames);
            Client.StatusChanged += s => { LastStatus = s; if (s.Contains("已停止共享")) StopNotified = true; };
            Client.SignInStarted += sec => SignInStartedSeconds = sec;
            Client.SignInEnded += () => SignInEndedReceived = true;
            Client.SignInResultReceived += (ok, reason) => { SignInSuccess = ok; SignInResultText = reason; };
            return this;
        }

        public void ResetSignInResult()
        {
            SignInSuccess = false;
            SignInResultText = null;
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

    // ---------- 5b. 定时签到 ----------
    private static async Task SignInE2E()
    {
        int port = FreePort();
        string recordsDirectory = Path.Combine(Path.GetTempPath(), "ScreenShareSmoke", Guid.NewGuid().ToString("N"));
        var server = new StreamServer(recordsDirectory);
        server.Start("127.0.0.1", port);

        var c1 = new TestClient().ConnectEvents();
        await c1.Connect("127.0.0.1", port, "小明");

        // 未发起签到时提交 → 应被拒绝
        await c1.Client.SubmitSignInAsync("S000", "小明");
        await WaitUntil(() => c1.SignInResultText != null, "未发起时提交应收到拒绝结果", 5_000);
        Check(!c1.SignInSuccess && c1.SignInResultText!.Contains("签到"), $"未发起时提交应失败，实际：{c1.SignInResultText}");

        // 发起 1 分钟签到
        server.StartSignIn(1);
        await WaitUntil(() => c1.SignInStartedSeconds > 0, "客户端1 收到签到开始", 5_000);
        Check(c1.SignInStartedSeconds is >= 55 and <= 60, $"剩余秒数应接近 60，实际 {c1.SignInStartedSeconds}");

        // 正常签到成功（注意：等"成功"状态出现，避免读到前一次提交的旧结果）
        c1.ResetSignInResult();
        await c1.Client.SubmitSignInAsync("S001", "小明");
        await WaitUntil(() => c1.SignInSuccess, "S001 应签到成功", 5_000);
        Check(server.SignInCount == 1, "服务端应有 1 条记录");

        // 中途连入的学生同样收到签到通知
        var c2 = new TestClient().ConnectEvents();
        await c2.Connect("127.0.0.1", port, "小红");
        await WaitUntil(() => c2.SignInStartedSeconds > 0, "中途加入的客户端2 收到签到开始", 5_000);
        Check(c2.SignInStartedSeconds is > 0 and <= 60, $"中途加入应收到剩余时间，实际 {c2.SignInStartedSeconds}");

        // 相同学号 → 拒绝（一人/一个学号只能签一次）
        c2.ResetSignInResult();
        await c2.Client.SubmitSignInAsync("S001", "小红");
        await WaitUntil(() => c2.SignInResultText != null, "重复学号应收到结果", 5_000);
        Check(!c2.SignInSuccess && c2.SignInResultText!.Contains("已完成签到"),
            $"重复学号应被拒绝，实际：{c2.SignInResultText}");
        Check(server.SignInCount == 1, "重复学号不应新增记录");

        // 不同学号 → 成功
        c2.ResetSignInResult();
        await c2.Client.SubmitSignInAsync("S002", "小红");
        await WaitUntil(() => c2.SignInSuccess, "S002 应签到成功", 5_000);
        Check(server.SignInCount == 2, "服务端应有 2 条记录");

        // 提前结束
        server.EndSignIn();
        await WaitUntil(() => c1.SignInEndedReceived && c2.SignInEndedReceived, "学生应收到签到结束", 5_000);

        // 记录核对
        var rec = server.GetSignInRecords();
        Check(rec.Count == 2 && rec.Any(r => r.StudentId == "S001" && r.Name == "小明")
                              && rec.Any(r => r.StudentId == "S002" && r.Name == "小红"),
            $"签到记录内容不符：{string.Join("、", rec.Select(r => r.StudentId + "/" + r.Name))}");
        var firstFile = Directory.GetFiles(recordsDirectory, "*.json").Single();
        using (var firstRound = JsonDocument.Parse(File.ReadAllText(firstFile)))
        {
            var saved = firstRound.RootElement.EnumerateArray().ToArray();
            Check(saved.Length == 2 && saved[0].GetProperty("StudentId").GetString() == "S001"
                && saved[1].GetProperty("StudentId").GetString() == "S002",
                "第一轮签到没有完整保存到文件");
        }

        // 结束后提交 → 拒绝
        var c3 = new TestClient().ConnectEvents();
        await c3.Connect("127.0.0.1", port, "小刚");
        await c3.Client.SubmitSignInAsync("S003", "小刚");
        await WaitUntil(() => c3.SignInResultText != null, "结束后提交应收到结果", 5_000);
        Check(!c3.SignInSuccess && c3.SignInResultText!.Contains("签到已结束"),
            $"结束后提交应被拒绝，实际：{c3.SignInResultText}");

        // 再次发起 → 记录清空
        server.StartSignIn(1);
        Check(server.SignInCount == 0, "重新发起签到应清空上次记录");
        Check(Directory.GetFiles(recordsDirectory, "*.json").Length == 2,
            "新一轮签到应另存文件并保留上一轮记录");
        using (var firstRoundAgain = JsonDocument.Parse(File.ReadAllText(firstFile)))
            Check(firstRoundAgain.RootElement.GetArrayLength() == 2,
                "新一轮签到覆盖了上一轮文件");
        var secondFile = Directory.GetFiles(recordsDirectory, "*.json").Single(p => p != firstFile);
        string blockedTempPath = secondFile + ".tmp";
        Directory.CreateDirectory(blockedTempPath);
        try
        {
            c3.ResetSignInResult();
            await c3.Client.SubmitSignInAsync("S004", "小刚");
            await WaitUntil(() => c3.SignInResultText != null, "无法保存时应通知学生失败", 5_000);
            Check(!c3.SignInSuccess && c3.SignInResultText!.Contains("保存失败") && server.SignInCount == 0,
                "保存失败时不应返回签到成功或把学生计入名单");
        }
        finally { Directory.Delete(blockedTempPath); }
        c3.ResetSignInResult();
        await c3.Client.SubmitSignInAsync("S004", "小刚");
        await WaitUntil(() => c3.SignInSuccess, "保存恢复后应能重试签到", 5_000);
        using (var secondRound = JsonDocument.Parse(File.ReadAllText(secondFile)))
            Check(secondRound.RootElement.GetArrayLength() == 1, "第二轮记录没有保存成功");
        server.EndSignIn();

        server.Stop();
        c1.Client.Disconnect();
        c2.Client.Disconnect();
        c3.Client.Disconnect();
        Console.Write("（2 人签到：S001/小明、S002/小红）");
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
