using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace ScreenShare.Core;

/// <summary>
/// 学生端：连接老师、接收帧、后台解码为位图后通过事件交给 UI 显示。
/// 所有网络与解码操作都在后台线程，不阻塞 UI。
/// </summary>
public sealed class StreamClient
{
    /// <summary>连接超时。</summary>
    public const int ConnectTimeoutMs = 8000;

    /// <summary>解码队列上限：积压时丢最旧帧，保证画面始终追赶最新。</summary>
    private const int DecodeQueueCapacity = 2;

    /// <summary>状态变化（中文文本，可直接显示）。</summary>
    public event Action<string>? StatusChanged;

    /// <summary>一帧解码完成（位图所有权交给订阅者，由其负责 Dispose）。</summary>
    public event Action<Bitmap>? FrameDecoded;

    /// <summary>收帧速率统计（每秒一次，-1 表示不在直播中）。</summary>
    public event Action<int>? FpsChanged;

    /// <summary>连接异常断开（主动断开不触发）。</summary>
    public event Action<string>? ConnectionLost;

    public bool IsConnected { get; private set; }
    public bool IsStreaming { get; private set; }

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Channel<byte[]> _decodeQueue =
        Channel.CreateBounded<byte[]>(new BoundedChannelOptions(DecodeQueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest });
    private volatile bool _userClosed;

    /// <summary>
    /// 连接老师端。失败时抛出带中文说明的异常，UI 直接展示。
    /// </summary>
    public async Task ConnectAsync(string hostInput, int port, string studentName, CancellationToken externalCt)
    {
        if (string.IsNullOrWhiteSpace(studentName))
            throw new ArgumentException("请先输入学生姓名");
        if (string.IsNullOrWhiteSpace(hostInput))
            throw new ArgumentException("请先输入老师电脑的 IP 地址");

        var addr = ResolveHost(hostInput.Trim());

        _tcp = new TcpClient();
        ConfigureSocket(_tcp);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        timeoutCts.CancelAfter(ConnectTimeoutMs);
        try
        {
            await _tcp.ConnectAsync(addr, port, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!externalCt.IsCancellationRequested)
        {
            Cleanup();
            throw new InvalidOperationException($"连接超时（{ConnectTimeoutMs / 1000} 秒）：无法连上老师电脑。请检查 IP 地址、端口是否正确，老师是否已打开本软件，以及防火墙是否放行。");
        }
        catch (Exception ex) when (ex is SocketException)
        {
            Cleanup();
            throw new InvalidOperationException("无法连接到老师电脑：" + SocketErrorToChinese(((SocketException)ex).SocketErrorCode));
        }

        _stream = _tcp.GetStream();
        IsConnected = true;

        var hello = new byte[1 + Encoding.UTF8.GetByteCount(studentName)];
        hello[0] = Protocol.Version;
        Encoding.UTF8.GetBytes(studentName, hello.AsSpan(1));
        bool ok = await SendAsync(Protocol.ClientHello, hello, externalCt).ConfigureAwait(false);
        if (!ok) { Cleanup(); throw new InvalidOperationException("发送连接请求失败"); }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => ReceiveLoop(token), CancellationToken.None);
        _ = Task.Run(() => PingLoop(token), CancellationToken.None);
        _ = Task.Run(DecodeLoop, CancellationToken.None);
    }

    /// <summary>学生主动断开。不触发 ConnectionLost 事件。</summary>
    public void Disconnect()
    {
        _userClosed = true;
        Cleanup();
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (type, payload) = await Protocol.ReadMessageAsync(_stream!, ct).ConfigureAwait(false);
                switch (type)
                {
                    case Protocol.ServerWelcome:
                        StatusChanged?.Invoke("已连接到老师电脑");
                        break;
                    case Protocol.Status:
                        IsStreaming = payload.Length > 0 && payload[0] == 1;
                        StatusChanged?.Invoke(IsStreaming ? "正在观看老师的屏幕" : "老师已停止共享，等待老师重新开始…");
                        break;
                    case Protocol.Frame:
                        if (IsStreaming) _decodeQueue.Writer.TryWrite(payload);
                        break;
                    case Protocol.Rejected:
                        throw new InvalidOperationException(
                            payload.Length > 0 ? Encoding.UTF8.GetString(payload) : "被老师端拒绝连接");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            bool wasConnected = IsConnected;
            IsConnected = false;
            IsStreaming = false;
            if (!_userClosed && wasConnected)
                ConnectionLost?.Invoke(ex is EndOfStreamException or IOException
                    ? "与老师电脑的连接已断开（老师可能已退出或网络中断）"
                    : "连接异常：" + ex.Message);
        }
    }

    /// <summary>后台解码线程：JPEG → Bitmap，积压时丢旧帧，画面始终追最新。每秒上报一次帧率。</summary>
    private async Task DecodeLoop()
    {
        int fpsCount = 0;
        var lastReport = Environment.TickCount64;
        await foreach (var jpeg in _decodeQueue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                var ms = new MemoryStream(jpeg); // Bitmap 持有该流，随位图一起存活
                var bmp = new Bitmap(ms);
                fpsCount++;
                FrameDecoded?.Invoke(bmp);
            }
            catch (ArgumentException) { /* 损坏帧，跳过 */ }

            var now = Environment.TickCount64;
            if (now - lastReport >= 1000)
            {
                FpsChanged?.Invoke(IsStreaming ? (int)Math.Round(fpsCount * 1000.0 / (now - lastReport)) : -1);
                fpsCount = 0;
                lastReport = now;
            }
        }
    }

    private async Task PingLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(4000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (!await SendAsync(Protocol.Ping, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false)) return;
        }
    }

    private async Task<bool> SendAsync(byte type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (_stream is null) return false;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Protocol.WriteAsync(_stream, type, payload, ct).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
        finally { _writeLock.Release(); }
    }

    private static IPAddress ResolveHost(string input)
    {
        if (IPAddress.TryParse(input, out var addr) && addr.AddressFamily == AddressFamily.InterNetwork)
            return addr;
        // 允许输入主机名（如老师电脑名），但最常见的用法是 IP
        try
        {
            var entry = Dns.GetHostEntry(input);
            var v4 = entry.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (v4 != null) return v4;
        }
        catch { /* 落到统一错误 */ }
        throw new ArgumentException("IP 地址格式不正确，请输入形如 192.168.1.100 的地址");
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
        catch { /* 忽略 */ }
    }

    private static string SocketErrorToChinese(SocketError error) => error switch
    {
        SocketError.ConnectionRefused => "连接被拒绝。请确认老师已打开本软件并在 Server 页点击了开始共享，IP 和端口正确。",
        SocketError.HostUnreachable or SocketError.NetworkUnreachable => "网络不可达。请确认两台电脑连的是同一个局域网。",
        SocketError.TimedOut => "连接超时，请检查 IP 地址和网络。",
        _ => $"网络错误（{error}）。",
    };

    private void Cleanup()
    {
        IsConnected = false;
        IsStreaming = false;
        try { _cts?.Cancel(); } catch { }
        _decodeQueue.Writer.TryComplete();
        try { _tcp?.Close(); } catch { }
        _stream = null;
        _tcp = null;
    }
}
