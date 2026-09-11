using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Yf.Api.Modules.Identity;

public sealed class CaptchaService
{
    private const int Width = 192, Height = 60, Capacity = 4096;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, Challenge> _items = new();
    private readonly ConcurrentDictionary<string, Window> _rates = new();

    public CaptchaResponse Issue(string clientIp)
    {
        if (!Allow("captcha:" + clientIp, 30)) throw Yf.Api.Infrastructure.ApiException.BadRequest("验证码请求过于频繁，请稍后再试");
        Prune();
        var code = string.Concat(Enumerable.Range(0, 6).Select(_ => "23456789"[RandomNumberGenerator.GetInt32(8)]));
        var id = Guid.NewGuid().ToString();
        _items[id] = new(code, DateTimeOffset.UtcNow.Add(Ttl));
        return new(id, "data:image/png;base64," + Convert.ToBase64String(Render(code)));
    }

    public bool Verify(string? id, string? code)
    {
        if (string.IsNullOrWhiteSpace(id) || !_items.TryRemove(id, out var item)) return false;
        var supplied = (code ?? "").Trim().ToUpperInvariant();
        if (supplied.Length != item.Code.Length || supplied.Any(c => c > 127)) return false;
        return item.ExpiresAt > DateTimeOffset.UtcNow &&
               CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(item.Code),
                   System.Text.Encoding.ASCII.GetBytes(supplied));
    }

    public bool AllowLogin(string clientIp, string employeeNo) =>
        Allow($"login:{clientIp}:{employeeNo}", 10) && Allow("login:" + clientIp, 60);

    private bool Allow(string key, int limit)
    {
        var now = DateTimeOffset.UtcNow;
        if (_rates.Count >= 8192)
        {
            foreach (var expired in _rates.Where(x => now - x.Value.Start >= TimeSpan.FromMinutes(1))) _rates.TryRemove(expired.Key, out _);
            while (_rates.Count >= 8192)
            {
                var oldest = _rates.MinBy(x => x.Value.Start);
                if (string.IsNullOrEmpty(oldest.Key) || !_rates.TryRemove(oldest.Key, out _)) break;
            }
        }
        var window = _rates.AddOrUpdate(key, _ => new(now, 1), (_, old) => now - old.Start >= TimeSpan.FromMinutes(1) ? new(now, 1) : old with { Count = old.Count + 1 });
        return window.Count <= limit;
    }

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in _items.Where(x => x.Value.ExpiresAt <= now)) _items.TryRemove(item.Key, out _);
        while (_items.Count >= Capacity)
        {
            var oldest = _items.MinBy(x => x.Value.ExpiresAt);
            if (string.IsNullOrEmpty(oldest.Key) || !_items.TryRemove(oldest.Key, out _)) break;
        }
    }

    private static byte[] Render(string code)
    {
        var rgb = new byte[Width * Height * 3];
        Array.Fill(rgb, (byte)242);
        for (var i = 0; i < code.Length; i++) DrawDigit(rgb, code[i] - '0', 13 + i * 29, 10);
        for (var i = 0; i < 360; i++) Set(rgb, RandomNumberGenerator.GetInt32(Width), RandomNumberGenerator.GetInt32(Height), (byte)RandomNumberGenerator.GetInt32(80, 190));
        var raw = new byte[(Width * 3 + 1) * Height];
        for (var y = 0; y < Height; y++) Buffer.BlockCopy(rgb, y * Width * 3, raw, y * (Width * 3 + 1) + 1, Width * 3);
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Span<byte> ihdr = stackalloc byte[13];
        WriteBe(ihdr, 0, Width); WriteBe(ihdr, 4, Height); ihdr[8] = 8; ihdr[9] = 2;
        Chunk(output, "IHDR", ihdr.ToArray());
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) z.Write(raw);
        Chunk(output, "IDAT", compressed.ToArray()); Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static readonly int[][] Segments =
    [
        [0,1,2,3,4,5], [1,2], [0,1,6,4,3], [0,1,6,2,3], [5,6,1,2],
        [0,5,6,2,3], [0,5,6,4,2,3], [0,1,2], [0,1,2,3,4,5,6], [0,1,2,3,5,6]
    ];
    private static void DrawDigit(byte[] rgb, int digit, int x, int y)
    {
        foreach (var s in Segments[digit])
        {
            var (sx, sy, ex, ey) = s switch
            {
                0 => (x + 3, y, x + 16, y + 3), 1 => (x + 16, y + 3, x + 19, y + 20),
                2 => (x + 16, y + 22, x + 19, y + 39), 3 => (x + 3, y + 39, x + 16, y + 42),
                4 => (x, y + 22, x + 3, y + 39), 5 => (x, y + 3, x + 3, y + 20),
                _ => (x + 3, y + 20, x + 16, y + 23)
            };
            for (var py = sy; py <= ey; py++) for (var px = sx; px <= ex; px++) Set(rgb, px, py, 35);
        }
    }
    private static void Set(byte[] data, int x, int y, byte v)
    {
        if ((uint)x >= Width || (uint)y >= Height) return;
        var p = (y * Width + x) * 3; data[p] = v; data[p + 1] = (byte)Math.Min(255, v + 8); data[p + 2] = (byte)Math.Min(255, v + 20);
    }
    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4]; WriteBe(len, 0, data.Length); s.Write(len);
        var name = System.Text.Encoding.ASCII.GetBytes(type); s.Write(name); s.Write(data);
        var crcData = name.Concat(data).ToArray(); Span<byte> crc = stackalloc byte[4]; WriteBe(crc, 0, unchecked((int)Crc32(crcData))); s.Write(crc);
    }
    private static uint Crc32(byte[] data)
    {
        var crc = 0xffffffffu;
        foreach (var b in data) { crc ^= b; for (var k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1)); }
        return ~crc;
    }
    private static void WriteBe(Span<byte> dst, int offset, int value) { dst[offset] = (byte)(value >> 24); dst[offset + 1] = (byte)(value >> 16); dst[offset + 2] = (byte)(value >> 8); dst[offset + 3] = (byte)value; }
    private sealed record Challenge(string Code, DateTimeOffset ExpiresAt);
    private sealed record Window(DateTimeOffset Start, int Count);
}
