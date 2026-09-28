using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ScreenShare.Core;

/// <summary>一个已接入的学生连接。</summary>
public sealed class ClientSession
{
    /// <summary>控制消息最多积压的条数。积压过多时断开慢客户端。</summary>
    public const int ControlQueueCapacity = 32;

    /// <summary>发送写超时：超过即视为网络过慢，断开该学生以保护整体。</summary>
    public const int SendTimeoutMs = 15000;

    public long Id { get; }
    public string Name { get; }
    public TcpClient Tcp { get; }
    public DateTime ConnectedAt { get; } = DateTime.Now;
    public volatile bool Disconnected;

    private readonly object _sendGate = new();
    private readonly Queue<byte[]> _controlQueue = new();
    private byte[]? _latestFrame;

    public ClientSession(long id, string name, TcpClient tcp)
    {
        Id = id;
        Name = name;
        Tcp = tcp;
    }

    /// <summary>控制消息按顺序可靠发送；画面只保留最新一帧，永不阻塞采集线程。</summary>
    public bool Enqueue(byte[] msg)
    {
        bool overflow = false;
        lock (_sendGate)
        {
            if (Disconnected) return false;
            if (msg[0] == Protocol.Frame)
                _latestFrame = msg;
            else if (_controlQueue.Count < ControlQueueCapacity)
                _controlQueue.Enqueue(msg);
            else
                overflow = true;
            if (!overflow) Monitor.Pulse(_sendGate);
        }
        if (overflow) MarkDisconnected();
        return !overflow;
    }

    /// <summary>仅由该学生的发送线程调用。先发控制消息，再发最新画面。</summary>
    internal byte[]? TakeNextMessage()
    {
        lock (_sendGate)
        {
            while (!Disconnected && _controlQueue.Count == 0 && _latestFrame is null)
                Monitor.Wait(_sendGate);
            if (Disconnected) return null;
            if (_controlQueue.Count > 0) return _controlQueue.Dequeue();
            var frame = _latestFrame;
            _latestFrame = null;
            return frame;
        }
    }

    public void MarkDisconnected()
    {
        lock (_sendGate)
        {
            if (Disconnected) return;
            Disconnected = true;
            _controlQueue.Clear();
            _latestFrame = null;
            Monitor.PulseAll(_sendGate);
        }
        try { Tcp.Close(); } catch { /* 忽略 */ }
    }
}

/// <summary>
/// 老师端流服务器：监听 TCP、完成握手、维护在线名单、
/// 把采集编码线程产出的帧复用分发到每个学生的独立发送队列。
/// </summary>
public sealed class StreamServer
{
    /// <summary>老师端 JPEG 画质（1-100）。70 在清晰度与带宽间平衡。</summary>
    public const int JpegQuality = 70;

    public event Action<string>? Log;
    public event Action? ClientsChanged;

    /// <summary>每秒统计：编码帧率、全部学生分发帧率合计、累计发送字节。</summary>
    public event Action<int, int, long>? StatsUpdated;

    /// <summary>签到状态或签到人数变化（发起/结束/有学生签到成功）。</summary>
    public event Action? SignInUpdated;

    private readonly object _gate = new();
    private readonly List<ClientSession> _clients = new();

    // ---- 签到状态 ----
    private readonly object _signInGate = new();
    private readonly List<(string StudentId, string Name, DateTime At)> _signInRecords = new();
    private readonly HashSet<string> _signInIds = new(StringComparer.Ordinal);
    private string? _signInRecordPath;
    private bool _signInActive;
    private DateTime _signInEndUtc;
    private CancellationTokenSource? _signInTimerCts;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private CaptureEncodeLoop? _captureLoop;
    private long _nextId;
    private long _sentFrames;
    private long _sentBytes;
    private volatile bool _streaming;

    public bool IsListening { get; private set; }
    public bool IsStreaming => _streaming;
    public int ClientCount { get { lock (_gate) return _clients.Count; } }

    // ---- 签到状态查询 ----
    public bool SignInActive { get { lock (_signInGate) return _signInActive; } }
    public int SignInCount { get { lock (_signInGate) return _signInRecords.Count; } }

    /// <summary>自动保存的每轮签到记录所在目录。</summary>
    public string SignInRecordsDirectory { get; }

    public StreamServer(string? signInRecordsDirectory = null)
    {
        SignInRecordsDirectory = signInRecordsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "教室屏幕共享", "签到记录");
    }

    /// <summary>签到剩余秒数（未在进行中时为 0）。</summary>
    public int SignInRemainingSeconds
    {
        get
        {
            lock (_signInGate)
                return _signInActive ? Math.Max(0, (int)Math.Ceiling((_signInEndUtc - DateTime.UtcNow).TotalSeconds)) : 0;
        }
    }

    /// <summary>签到记录快照（按提交顺序）。</summary>
    public IReadOnlyList<(string StudentId, string Name, DateTime At)> GetSignInRecords()
    {
        lock (_signInGate)
            return _signInRecords.ToList();
    }

    /// <summary>发起定时签到（1-30 分钟）。重新发起会清空上一次的记录。</summary>
    public void StartSignIn(int minutes)
    {
        if (!IsListening) throw new InvalidOperationException("服务未启动");
        int seconds = Math.Clamp(minutes, 1, 30) * 60;
        Directory.CreateDirectory(SignInRecordsDirectory);
        string recordPath = Path.Combine(SignInRecordsDirectory,
            $"签到_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.json");
        SaveSignInRecords(recordPath, []);
        lock (_signInGate)
        {
            _signInRecords.Clear();
            _signInIds.Clear();
            _signInRecordPath = recordPath;
            _signInActive = true;
            _signInEndUtc = DateTime.UtcNow.AddSeconds(seconds);
        }
        Broadcast(Protocol.SignInStart, BitConverter.GetBytes((uint)seconds));
        SignInUpdated?.Invoke();
        Log?.Invoke($"发起签到，时长 {minutes} 分钟");
        Log?.Invoke($"签到记录自动保存至 {recordPath}");

        _signInTimerCts?.Cancel();
        _signInTimerCts = CancellationTokenSource.CreateLinkedTokenSource(_cts!.Token);
        var token = _signInTimerCts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            EndSignIn();
        }, CancellationToken.None);
    }

    /// <summary>结束签到（手动提前结束或到点自动结束），通知所有学生。</summary>
    public void EndSignIn()
    {
        bool wasActive;
        lock (_signInGate) { wasActive = _signInActive; _signInActive = false; }
        if (!wasActive) return;
        try { _signInTimerCts?.Cancel(); } catch { }
        Broadcast(Protocol.SignInEnd, ReadOnlySpan<byte>.Empty);
        SignInUpdated?.Invoke();
        Log?.Invoke($"签到已结束，共 {SignInCount} 人签到");
    }

    /// <summary>处理学生提交的签到，结果消息回发给该学生。学号唯一，一人（一个学号）只能签到一次。</summary>
    private void ProcessSignInSubmit(ClientSession session, byte[] payload)
    {
        (bool ok, string reason) = RegisterSignIn(payload);
        var reasonBytes = Encoding.UTF8.GetBytes(reason);
        var resp = new byte[1 + reasonBytes.Length];
        resp[0] = ok ? (byte)0 : (byte)1;
        reasonBytes.CopyTo(resp, 1);
        session.Enqueue(Protocol.BuildMessage(Protocol.SignInResult, resp));
    }

    private (bool Ok, string Reason) RegisterSignIn(byte[] payload)
    {
        if (payload.Length < 1) return (false, "签到数据无效");
        int idLen = payload[0];
        if (idLen == 0 || payload.Length < 1 + idLen + 1) return (false, "请填写学号");
        string studentId = Encoding.UTF8.GetString(payload, 1, idLen).Trim();
        int nameLen = payload[1 + idLen];
        if (payload.Length < 1 + idLen + 1 + nameLen) return (false, "签到数据无效");
        string name = Encoding.UTF8.GetString(payload, 1 + idLen + 1, nameLen).Trim();

        lock (_signInGate)
        {
            if (!_signInActive) return (false, "签到已结束");
            if (studentId.Length == 0) return (false, "请填写学号");
            if (_signInIds.Contains(studentId)) return (false, $"学号 {studentId} 已完成签到");
            var record = (StudentId: studentId, Name: name.Length > 0 ? name : "未署名", At: DateTime.Now);
            try
            {
                SaveSignInRecords(_signInRecordPath!, [.. _signInRecords, record]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log?.Invoke($"签到记录保存失败：{ex.Message}");
                return (false, "签到记录保存失败，请联系老师后重试");
            }
            _signInIds.Add(studentId);
            _signInRecords.Add(record);
        }
        SignInUpdated?.Invoke();
        Log?.Invoke($"学生 [{name}] 签到成功（学号 {studentId}）");
        return (true, "签到成功");
    }

    private static void SaveSignInRecords(string path,
        IReadOnlyList<(string StudentId, string Name, DateTime At)> records)
    {
        var data = records.Select(r => new { r.StudentId, r.Name, r.At });
        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tempPath, path, overwrite: true);
    }

    private void Broadcast(byte type, ReadOnlySpan<byte> payload)
    {
        Deliver(Protocol.BuildMessage(type, payload));
    }

    /// <summary>开始监听。端口被占用等情况抛 SocketException（UI 层转中文提示）。</summary>
    public void Start(string ipAddress, int port)
    {
        if (IsListening) throw new InvalidOperationException("服务已在运行");
        var addr = IPAddress.Parse(ipAddress);
        _listener = new TcpListener(addr, port);
        _listener.Start(); // 端口被占用时在这里抛出
        IsListening = true;
        _cts = new CancellationTokenSource();
        _sentFrames = 0;
        _sentBytes = 0;
        _ = AcceptLoopAsync(_cts.Token);
        _ = StatsLoopAsync(_cts.Token);
        Log?.Invoke($"服务已启动，监听 {ipAddress}:{port}");
    }

    /// <summary>开始（或恢复）屏幕共享。可重复调用以恢复共享；fps 运行中修改即时生效。</summary>
    public void StartStreaming(int fps)
    {
        if (!IsListening) throw new InvalidOperationException("服务未启动");
        if (_captureLoop is null || !_captureLoop.IsRunning)
        {
            var old = _captureLoop;
            old?.Dispose();
            _captureLoop = new CaptureEncodeLoop(Deliver, ex => Log?.Invoke($"屏幕采集异常：{ex.Message}"), _cts!.Token)
            {
                TargetFps = fps,
                Quality = JpegQuality,
            };
            _captureLoop.Start();
            Log?.Invoke($"开始共享屏幕，目标帧率 {fps} FPS");
        }
        else if (_captureLoop.TargetFps != fps)
        {
            _captureLoop.TargetFps = fps;
            Log?.Invoke($"目标帧率已切换为 {fps} FPS");
        }
        _streaming = true;
        BroadcastStatus();
    }

    /// <summary>停止共享（保持监听与学生连接，学生收到提示后等待老师重新开始）。</summary>
    public void StopStreaming()
    {
        if (!_streaming) return;
        _streaming = false;
        _captureLoop?.Stop();
        BroadcastStatus();
        Log?.Invoke("已停止共享屏幕（学生仍保持连接，可等待重新开始）");
    }

    /// <summary>完全停止：关闭监听、断开所有学生、释放采集资源。窗口关闭时调用。</summary>
    public void Stop()
    {
        _streaming = false;
        IsListening = false;
        lock (_signInGate) _signInActive = false;
        try { _signInTimerCts?.Cancel(); } catch { }
        _captureLoop?.Dispose();
        _captureLoop = null;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        lock (_gate)
        {
            foreach (var c in _clients) c.MarkDisconnected();
            _clients.Clear();
        }
        ClientsChanged?.Invoke();
    }

    /// <summary>在线学生详情：显示名（同名自动编号：张三、张三(2)…）与连接时间。</summary>
    public IReadOnlyList<(string DisplayName, DateTime ConnectedAt)> GetClientDetails()
    {
        List<(long Id, string Name, DateTime At)> list;
        lock (_gate)
            list = _clients.Select(c => (c.Id, c.Name, c.ConnectedAt)).ToList();

        var result = new List<(string, DateTime)>(list.Count);
        foreach (var group in list.GroupBy(x => x.Name))
        {
            var members = group.OrderBy(x => x.Id).ToList();
            for (int i = 0; i < members.Count; i++)
                result.Add((i == 0 ? members[i].Name : $"{members[i].Name}({i + 1})", members[i].At));
        }
        return result;
    }

    private void Deliver(byte[] msg)
    {
        ClientSession[] snapshot;
        lock (_gate)
            snapshot = _clients.ToArray();
        foreach (var s in snapshot)
            s.Enqueue(msg);
    }

    private void BroadcastStatus()
    {
        Deliver(Protocol.BuildMessage(Protocol.Status, [(byte)(_streaming ? 1 : 0)]));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener!;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                ConfigureSocket(tcp);
                _ = HandleClientAsync(tcp, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* 监听已关闭 */ }
    }

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
    {
        ClientSession? session = null;
        try
        {
            using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            helloCts.CancelAfter(10_000); // 10 秒内未完成握手即断开
            var stream = tcp.GetStream();
            var (type, payload) = await Protocol.ReadMessageAsync(stream, helloCts.Token).ConfigureAwait(false);

            if (type != Protocol.ClientHello || payload.Length < 1)
            {
                await SendRejected(stream, "握手消息无效").ConfigureAwait(false);
                return;
            }
            if (payload[0] != Protocol.Version)
            {
                await SendRejected(stream, "软件版本不一致，请所有电脑使用相同版本的软件").ConfigureAwait(false);
                return;
            }

            var name = Encoding.UTF8.GetString(payload, 1, payload.Length - 1).Trim();
            if (name.Length == 0) name = "未命名";

            session = new ClientSession(Interlocked.Increment(ref _nextId), name, tcp);
            bool added;
            lock (_gate)
            {
                if (_clients.Count >= 100) throw new InvalidOperationException("连接数已达上限");
                _clients.Add(session);
                added = true;
            }
            if (added) ClientsChanged?.Invoke();
            Log?.Invoke($"学生 [{name}] 已连接");

            // 欢迎与当前共享状态进入发送队列（由该学生的发送线程写出）
            session.Enqueue(Protocol.BuildMessage(Protocol.ServerWelcome, ReadOnlySpan<byte>.Empty));
            BroadcastStatus();
            // 签到进行中：新连入的学生同样收到签到通知（带剩余时间）
            if (SignInActive)
            {
                session.Enqueue(Protocol.BuildMessage(Protocol.SignInStart,
                    BitConverter.GetBytes((uint)SignInRemainingSeconds)));
            }

            // 同步写使用独立后台线程，避免占住处理其他学生握手的线程池线程
            new Thread(() => SendLoop(session)) { IsBackground = true, Name = $"Send-{session.Id}" }.Start();

            // 接收循环：等待心跳/签到提交，检测对端断开
            while (!ct.IsCancellationRequested)
            {
                var (t, p) = await Protocol.ReadMessageAsync(stream, ct).ConfigureAwait(false);
                if (t == Protocol.Ping) continue;           // 心跳保活
                if (t == Protocol.SignInSubmit) ProcessSignInSubmit(session, p);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { /* 握手失败或连接异常，走 finally 清理 */ }
        finally
        {
            if (session != null) RemoveClient(session);
            try { tcp.Close(); } catch { }
        }
    }

    private static async Task SendRejected(NetworkStream stream, string reason)
    {
        try
        {
            await Protocol.WriteAsync(stream, Protocol.Rejected, Encoding.UTF8.GetBytes(reason), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch { /* 尽力而为 */ }
    }

    /// <summary>
    /// 每个学生一个专用发送线程。同步 Write 应用 WriteTimeout：
    /// 网络过慢写超时即断开该学生，其队列丢帧机制保证不拖慢其他学生。
    /// </summary>
    private void SendLoop(ClientSession s)
    {
        try
        {
            var stream = s.Tcp.GetStream();
            stream.WriteTimeout = ClientSession.SendTimeoutMs;
            byte[]? msg;
            while ((msg = s.TakeNextMessage()) is not null)
            {
                stream.Write(msg, 0, msg.Length);
                Interlocked.Increment(ref _sentFrames);
                Interlocked.Add(ref _sentBytes, msg.Length);
            }
        }
        catch (Exception)
        {
            // 写超时/断开/服务停止：断开该学生，不影响其他学生
        }
        finally
        {
            s.MarkDisconnected();
        }
    }

    private void RemoveClient(ClientSession s)
    {
        bool removed;
        lock (_gate) removed = _clients.Remove(s);
        if (removed)
        {
            s.MarkDisconnected();
            ClientsChanged?.Invoke();
            Log?.Invoke($"学生 [{s.Name}] 已断开");
        }
    }

    /// <summary>每秒向 UI 报告统计：编码帧率（0 表示未在共享）、分发帧率合计、累计发送字节。</summary>
    private async Task StatsLoopAsync(CancellationToken ct)
    {
        long lastEncoded = 0, lastSent = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            long encoded = _captureLoop?.EncodedFrames ?? 0;
            int encFps = (int)(encoded - lastEncoded);
            lastEncoded = encoded;

            long sent = Interlocked.Read(ref _sentFrames);
            int sentFps = (int)(sent - lastSent);
            lastSent = sent;

            StatsUpdated?.Invoke(_streaming ? encFps : 0, sentFps, Interlocked.Read(ref _sentBytes));
        }
    }

    private static void ConfigureSocket(TcpClient tcp)
    {
        try
        {
            tcp.NoDelay = true;
            var socket = tcp.Client;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 3);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch { /* 个别平台不支持时忽略 */ }
    }
}
