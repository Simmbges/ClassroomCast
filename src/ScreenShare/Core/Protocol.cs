using System.Buffers.Binary;
using System.Text;

namespace ScreenShare.Core;

/// <summary>
/// 自定义 TCP 二进制协议。
/// 每条消息：1 字节消息类型 + 4 字节小端载荷长度 + 载荷。
/// </summary>
public static class Protocol
{
    /// <summary>协议版本，两端不一致时拒绝连接。</summary>
    public const byte Version = 1;

    /// <summary>载荷长度上限：8 MB（4K 高画质 JPEG 约 1~3 MB，留裕量）。</summary>
    public const int MaxPayloadBytes = 8 * 1024 * 1024;

    /// <summary>学生姓名最大字节数（UTF-8）。</summary>
    public const int MaxNameBytes = 60;

    // 消息类型
    public const byte ClientHello = 0x01;   // C→S: [1字节版本][姓名UTF-8]
    public const byte ServerWelcome = 0x02; // S→C: 空
    public const byte Frame = 0x03;         // S→C: JPEG 数据
    public const byte Status = 0x04;        // S→C: [1字节: 0=已停止共享 1=直播中]
    public const byte Rejected = 0x05;      // S→C: 拒绝原因（UTF-8）
    public const byte Ping = 0x06;          // C→S: 空

    /// <summary>把一条消息组装成完整字节（头 + 载荷一次写出，减少分片）。</summary>
    public static byte[] BuildMessage(byte type, ReadOnlySpan<byte> payload)
    {
        var msg = new byte[5 + payload.Length];
        msg[0] = type;
        BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(1), payload.Length);
        payload.CopyTo(msg.AsSpan(5));
        return msg;
    }

    /// <summary>写出一条消息。</summary>
    public static async Task WriteAsync(Stream stream, byte type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await stream.WriteAsync(BuildMessage(type, payload.Span), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>读取一条消息。对端关闭或非法长度时抛异常。</summary>
    public static async Task<(byte Type, byte[] Payload)> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        var header = await ReadExactAsync(stream, 5, ct).ConfigureAwait(false);
        byte type = header[0];
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (length < 0 || length > MaxPayloadBytes)
            throw new InvalidDataException($"消息长度非法：{length}");
        byte[] payload = length == 0 ? Array.Empty<byte>() : await ReadExactAsync(stream, length, ct).ConfigureAwait(false);
        return (type, payload);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("连接已由对端关闭");
            read += n;
        }
        return buf;
    }
}
