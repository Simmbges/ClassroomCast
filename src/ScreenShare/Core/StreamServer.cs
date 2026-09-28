using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace ScreenShare.Core;

/// <summary>一个已接入的学生连接。</summary>
public sealed class ClientSession
{
    /// <summary>每个学生的发送队列上限（帧数）。满了丢最旧帧，保证实时性。</summary>
    public const int SendQueueCapacity = 3;

    /// <summary>发送写超时：超过即视为网络过慢，断开该学生以保护整体。</summary>
    public const int SendTimeoutMs = 15000;

    public long Id { get; }
    public string Name { get; }
    public TcpClient Tcp { get; }
    public DateTime ConnectedAt { get; } = DateTime.Now;
    public volatile bool Disconnected;

    /// <summary>
    /// 同步阻塞队列：发送线程用 GetConsumingEnumerable 取帧，同步 Write 受 WriteTimeout 约束，
    /// 慢客户端超时后可被踢除。
    /// </summary>
    internal readonly BlockingCollection<byte[]> _queue = new(new ConcurrentQueue<byte[]>(), SendQueueCapacity);

    public ClientSession(long id, string name, TcpClient tcp)
    {
        Id = id;
        Name = name;
        Tcp = tcp;
    }

    /// <summary>入队一条消息（帧/状态）。队列满时丢弃最旧的一条再入队，永不阻塞采集线程。</summary>
    public bool Enqueue(byte[] msg)
    {
        if (Disconnected) return false;
        if (_queue.TryAdd(msg)) return true;
        if (Disconnected) return false;
        _queue.TryTake(out _);      // 丢最旧一帧
        return _queue.TryAdd(msg);  // 极端并发下可能失败，视为丢帧
    }

    public void MarkDisconnected()
    {
        if (Disconnected) return;
        Disconnected = true;
        _queue.CompleteAdding();
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

    private readonly object _gate = new();
    private readonly List<ClientSession> _clients = new();

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

            // 发送线程：专用于该学生的帧写出
            _ = Task.Run(() => SendLoop(session), CancellationToken.None);

            // 接收循环：等待心跳，检测对端断开
            while (!ct.IsCancellationRequested)
            {
                var (t, _) = await Protocol.ReadMessageAsync(stream, ct).ConfigureAwait(false);
                if (t == Protocol.Ping) continue; // 心跳保活
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
            foreach (var msg in s._queue.GetConsumingEnumerable())
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
