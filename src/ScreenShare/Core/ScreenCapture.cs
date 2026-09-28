using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ScreenShare.Core;

/// <summary>
/// GDI 屏幕采集：把整个虚拟桌面（多显示器时为所有屏幕的拼合区域）
/// 复制到一张复用的位图上。调用方在同一线程内编码后即用。
/// </summary>
public sealed class ScreenCapture : IDisposable
{
    private readonly Rectangle _bounds;
    private readonly Bitmap _bitmap;
    private readonly Graphics _graphics;

    public ScreenCapture()
    {
        _bounds = SystemInformation.VirtualScreen;
        if (_bounds.Width <= 0 || _bounds.Height <= 0)
            throw new InvalidOperationException("无法获取屏幕尺寸");
        _bitmap = new Bitmap(_bounds.Width, _bounds.Height, PixelFormat.Format32bppArgb);
        _graphics = Graphics.FromImage(_bitmap);
    }

    public int Width => _bounds.Width;
    public int Height => _bounds.Height;

    /// <summary>抓取一帧，返回复用位图（不能跨线程持有，需在调用线程立即编码）。</summary>
    public Bitmap Capture()
    {
        _graphics.CopyFromScreen(_bounds.X, _bounds.Y, 0, 0, _bounds.Size, CopyPixelOperation.SourceCopy);
        DrawCursor();
        return _bitmap;
    }

    private void DrawCursor()
    {
        var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref cursor) || (cursor.Flags & 1) == 0 || !_bounds.Contains(cursor.Position))
            return;
        if (!GetIconInfo(cursor.Handle, out var icon)) return;
        try
        {
            IntPtr hdc = _graphics.GetHdc();
            try
            {
                DrawIconEx(hdc, cursor.Position.X - _bounds.X - (int)icon.HotspotX,
                    cursor.Position.Y - _bounds.Y - (int)icon.HotspotY,
                    cursor.Handle, 0, 0, 0, IntPtr.Zero, 0x0003);
            }
            finally { _graphics.ReleaseHdc(hdc); }
        }
        catch (ExternalException) { /* 鼠标指针偶发不可用时继续共享屏幕 */ }
        finally
        {
            if (icon.Mask != IntPtr.Zero) DeleteObject(icon.Mask);
            if (icon.Color != IntPtr.Zero) DeleteObject(icon.Color);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Handle;
        public Point Position;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public IntPtr Mask;
        public IntPtr Color;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CursorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr cursor, out IconInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon,
        int width, int height, uint step, IntPtr brush, uint flags);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    public void Dispose()
    {
        _graphics.Dispose();
        _bitmap.Dispose();
    }
}

/// <summary>GDI+ JPEG 编码。</summary>
public static class JpegEncoder
{
    private static readonly ImageCodecInfo Codec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public static byte[] Encode(Bitmap bmp, long quality)
    {
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        using var ms = new MemoryStream(128 * 1024);
        bmp.Save(ms, Codec, ep);
        return ms.ToArray();
    }
}

/// <summary>
/// 老师端采集-编码线程：按目标帧率抓屏、编码一次，把同一帧分发给所有学生。
/// 运行中修改目标帧率即时生效；编码异常或停止共享时线程退出。
/// </summary>
public sealed class CaptureEncodeLoop : IDisposable
{
    private readonly Action<byte[]> _deliver; // 收到完整帧消息后的分发回调（仅入队，同步返回）
    private readonly Action<Exception>? _onError;
    private readonly CancellationToken _ct;
    private Thread? _thread;
    private volatile bool _running;
    private long _encodedFrames;

    public int TargetFps { get; set; } = 15;
    public long Quality { get; set; } = 70;

    /// <summary>已编码帧计数（用于实际帧率统计）。</summary>
    public long EncodedFrames => Interlocked.Read(ref _encodedFrames);

    public CaptureEncodeLoop(Action<byte[]> deliver, Action<Exception>? onError, CancellationToken ct)
    {
        _deliver = deliver;
        _onError = onError;
        _ct = ct;
    }

    public bool IsRunning => _running;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "CaptureEncode" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        try { _thread?.Join(1000); } catch { /* 线程尚未启动等情形 */ }
        _thread = null;
    }

    private void Run()
    {
        try
        {
            using var capture = new ScreenCapture();
            while (_running && !_ct.IsCancellationRequested)
            {
                var start = Stopwatch.GetTimestamp();

                var bmp = capture.Capture();
                var jpeg = JpegEncoder.Encode(bmp, Quality);
                var frame = Protocol.BuildMessage(Protocol.Frame, jpeg);
                _deliver(frame);
                Interlocked.Increment(ref _encodedFrames);

                // 按当前目标帧率控制节奏（运行中修改 TargetFps 即时生效）
                int fps = Math.Clamp(TargetFps, 1, 60);
                double elapsedMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                int waitMs = (int)(1000.0 / fps - elapsedMs);
                if (waitMs > 0) Thread.Sleep(waitMs);
            }
        }
        catch (Exception ex)
        {
            if (!_ct.IsCancellationRequested)
                _onError?.Invoke(ex);
        }
        finally
        {
            _running = false;
        }
    }

    public void Dispose() => Stop();
}
