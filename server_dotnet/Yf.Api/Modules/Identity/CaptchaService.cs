using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Yf.Api.Modules.Identity;

public interface ICaptchaChallengeObserver
{
    void OnIssued(string captchaId, string answer);
}

public sealed class CaptchaService(IEnumerable<ICaptchaChallengeObserver> observers)
{
    private const int Width = 192, Height = 60, Capacity = 4096;
    private const string Letters = "ACDEFHJKLMNPRTUVWXY";
    private const string Alphabet = Letters + "347";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, Challenge> _items = new();
    private readonly ConcurrentDictionary<string, Window> _rates = new();

    public CaptchaResponse Issue(string clientIp)
    {
        if (!Allow("captcha:" + clientIp, 30)) throw Yf.Api.Infrastructure.ApiException.BadRequest("验证码请求过于频繁，请稍后再试");
        Prune();
        var code = CreateCode();
        var id = Guid.NewGuid().ToString();
        _items[id] = new(code, DateTimeOffset.UtcNow.Add(Ttl));
        foreach (var observer in observers)
        {
            try { observer.OnIssued(id, code); }
            catch { }
        }
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

    private static string CreateCode()
    {
        var value = new char[6];
        value[0] = Letters[RandomNumberGenerator.GetInt32(Letters.Length)];
        for (var i = 1; i < value.Length; i++) value[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        for (var i = value.Length - 1; i > 0; i--)
        {
            var swap = RandomNumberGenerator.GetInt32(i + 1);
            (value[i], value[swap]) = (value[swap], value[i]);
        }
        return new string(value);
    }

    private static byte[] Render(string code)
    {
        var rgb = new byte[Width * Height * 3];
        var background = (byte)RandomNumberGenerator.GetInt32(232, 252);
        for (var p = 0; p < rgb.Length; p += 3)
        {
            rgb[p] = background;
            rgb[p + 1] = (byte)Math.Max(0, background - RandomNumberGenerator.GetInt32(0, 8));
            rgb[p + 2] = (byte)Math.Max(0, background - RandomNumberGenerator.GetInt32(0, 8));
        }
        for (var i = 0; i < code.Length; i++)
        {
            var originX = 9 + i * 30 + RandomNumberGenerator.GetInt32(-3, 4);
            var originY = 8 + RandomNumberGenerator.GetInt32(-4, 5);
            DrawGlyph(rgb, code[i], originX, originY, RandomNumberGenerator.GetInt32(-3, 4),
                RandomNumberGenerator.GetInt32(18, 75), RandomNumberGenerator.GetInt32(0, 360));
        }
        for (var i = 0; i < 1; i++)
        {
            var y = RandomNumberGenerator.GetInt32(5, Height - 5);
            var amplitude = RandomNumberGenerator.GetInt32(2, 8);
            var phase = RandomNumberGenerator.GetInt32(0, 360) * Math.PI / 180d;
            var shade = (byte)RandomNumberGenerator.GetInt32(90, 190);
            for (var x = 0; x < Width; x++)
                Set(rgb, x, y + (int)Math.Round(Math.Sin(x / 13d + phase) * amplitude), shade);
        }
        for (var i = 0; i < 240; i++)
        {
            var shade = (byte)RandomNumberGenerator.GetInt32(70, 225);
            Set(rgb, RandomNumberGenerator.GetInt32(Width), RandomNumberGenerator.GetInt32(Height), shade);
        }
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

    private static readonly IReadOnlyDictionary<char, string[]> Glyphs = new Dictionary<char, string[]>
    {
        ['A']=["01110","10001","10001","11111","10001","10001","10001"], ['B']=["11110","10001","10001","11110","10001","10001","11110"],
        ['C']=["01111","10000","10000","10000","10000","10000","01111"], ['D']=["11110","10001","10001","10001","10001","10001","11110"],
        ['E']=["11111","10000","10000","11110","10000","10000","11111"], ['F']=["11111","10000","10000","11110","10000","10000","10000"],
        ['G']=["01111","10000","10000","10111","10001","10001","01110"], ['H']=["10001","10001","10001","11111","10001","10001","10001"],
        ['J']=["00111","00010","00010","00010","10010","10010","01100"], ['K']=["10001","10010","10100","11000","10100","10010","10001"],
        ['L']=["10000","10000","10000","10000","10000","10000","11111"], ['M']=["10001","11011","10101","10101","10001","10001","10001"],
        ['N']=["10001","11001","10101","10011","10001","10001","10001"], ['P']=["11110","10001","10001","11110","10000","10000","10000"],
        ['Q']=["01110","10001","10001","10001","10101","10010","01101"], ['R']=["11110","10001","10001","11110","10100","10010","10001"],
        ['S']=["01111","10000","10000","01110","00001","00001","11110"], ['T']=["11111","00100","00100","00100","00100","00100","00100"],
        ['U']=["10001","10001","10001","10001","10001","10001","01110"], ['V']=["10001","10001","10001","10001","10001","01010","00100"],
        ['W']=["10001","10001","10001","10101","10101","11011","10001"], ['X']=["10001","10001","01010","00100","01010","10001","10001"],
        ['Y']=["10001","10001","01010","00100","00100","00100","00100"], ['Z']=["11111","00001","00010","00100","01000","10000","11111"],
        ['2']=["01110","10001","00001","00010","00100","01000","11111"], ['3']=["11110","00001","00001","01110","00001","00001","11110"],
        ['4']=["00010","00110","01010","10010","11111","00010","00010"], ['5']=["11111","10000","10000","11110","00001","00001","11110"],
        ['6']=["01110","10000","10000","11110","10001","10001","01110"], ['7']=["11111","00001","00010","00100","01000","01000","01000"],
        ['8']=["01110","10001","10001","01110","10001","10001","01110"], ['9']=["01110","10001","10001","01111","00001","00001","01110"]
    };

    private static void DrawGlyph(byte[] rgb, char value, int x, int y, int shear, int shade, int phaseDegrees)
    {
        var glyph = Glyphs[value];
        var phase = phaseDegrees * Math.PI / 180d;
        for (var row = 0; row < glyph.Length; row++)
        {
            for (var column = 0; column < glyph[row].Length; column++)
            {
                if (glyph[row][column] != '1') continue;
                for (var dy = 0; dy < 6; dy++)
                for (var dx = 0; dx < 4; dx++)
                {
                    var py = y + row * 6 + dy;
                    var wave = (int)Math.Round(Math.Sin((py + column * 3) / 8d + phase) * 2);
                    var px = x + column * 4 + dx + shear * (row - 3) / 4 + wave;
                    Set(rgb, px, py, (byte)Math.Clamp(shade + RandomNumberGenerator.GetInt32(-8, 9), 0, 255));
                }
            }
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
