using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Identity;

public static class PasswordService
{
    public const int MaxPasswordBytes = 256;
    public const string PolicyMessage = "密码需 6-20 个字符，不能使用常见弱密码或简单重复序列";
    private const int MemoryKb = 19_456;
    private const int Iterations = 2;
    private const int Parallelism = 1;
    private const int HashBytes = 32;
    private static readonly SemaphoreSlim HashSlots = new(Math.Max(1, Math.Min(2, Environment.ProcessorCount)));
    private static readonly string[] BlockedStems =
    [
        "password", "passwd", "admin", "administrator", "qwerty", "qwertyuiop",
        "asdfghjkl", "letmein", "welcome", "changeme", "iloveyou", "monkey", "dragon",
        "football", "baseball", "abc", "abcd", "abcdef", "login", "test", "secret"
    ];

    public static void Validate(string password)
    {
        if (!StrongEnough(password))
            throw ApiException.BadRequest(PolicyMessage);
    }

    public static bool StrongEnough(string? password)
    {
        if (password is null) return false;
        var chars = password.EnumerateRunes().Count();
        if (chars is < 6 or > 20 || Encoding.UTF8.GetByteCount(password) > MaxPasswordBytes) return false;

        var end = password.Length;
        while (end > 0 && password[end - 1] <= 127 && (char.IsDigit(password[end - 1]) || IsAsciiPunctuation(password[end - 1]))) end--;
        var core = password[..end];
        var stem = new string(core.Select(NormalizeWeakChar).Where(c => char.IsAsciiLetter(c)).ToArray());
        if (BlockedStems.Any(word => stem.Length > 0 && stem.Length % word.Length == 0 &&
                                     string.Concat(Enumerable.Repeat(word, stem.Length / word.Length)) == stem))
            return false;
        if (string.Concat(Enumerable.Repeat("0123456789", 8)).Contains(password, StringComparison.Ordinal) ||
            string.Concat(Enumerable.Repeat("9876543210", 8)).Contains(password, StringComparison.Ordinal))
            return false;
        var runes = password.EnumerateRunes().ToArray();
        return !Enumerable.Range(1, 4).Any(period => runes.Select((r, i) => (r, i)).All(x => x.r == runes[x.i % period]));
    }

    public static async Task<string> HashAsync(string password, CancellationToken cancellationToken = default)
    {
        Validate(password);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = await DeriveAsync("argon2id", password, salt, MemoryKb, Iterations, Parallelism, HashBytes, cancellationToken);
        return $"$argon2id$v=19$m={MemoryKb},t={Iterations},p={Parallelism}${B64(salt)}${B64(hash)}";
    }

    public static async Task<bool> VerifyAsync(string password, string encodedHash, CancellationToken cancellationToken = default)
    {
        if (Encoding.UTF8.GetByteCount(password) > MaxPasswordBytes || !TryParse(encodedHash, out var phc)) return false;
        var actual = await DeriveAsync(phc.Algorithm, password, phc.Salt, phc.MemoryKb, phc.Iterations,
            phc.Parallelism, phc.Hash.Length, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(actual, phc.Hash);
    }

    private static async Task<byte[]> DeriveAsync(string algorithm, string password, byte[] salt, int memoryKb,
        int iterations, int parallelism, int hashBytes, CancellationToken cancellationToken)
    {
        await HashSlots.WaitAsync(cancellationToken);
        try
        {
            Argon2 argon = algorithm switch
            {
                "argon2id" => new Argon2id(Encoding.UTF8.GetBytes(password)),
                "argon2i" => new Argon2i(Encoding.UTF8.GetBytes(password)),
                "argon2d" => new Argon2d(Encoding.UTF8.GetBytes(password)),
                _ => throw new InvalidOperationException("Unsupported Argon2 algorithm")
            };
            argon.Salt = salt;
            argon.MemorySize = memoryKb;
            argon.Iterations = iterations;
            argon.DegreeOfParallelism = parallelism;
            return await argon.GetBytesAsync(hashBytes);
        }
        finally { HashSlots.Release(); }
    }

    private static bool TryParse(string value, out Phc phc)
    {
        phc = default;
        try
        {
            var parts = value.Split('$', StringSplitOptions.None);
            if (parts.Length != 6 || parts[0].Length != 0 || parts[2] != "v=19") return false;
            if (parts[1] is not ("argon2id" or "argon2i" or "argon2d")) return false;
            var args = parts[3].Split(',').Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => int.Parse(x[1]));
            var salt = FromB64(parts[4]);
            var hash = FromB64(parts[5]);
            if (!args.TryGetValue("m", out var memory) || !args.TryGetValue("t", out var iterations) ||
                !args.TryGetValue("p", out var parallelism) || memory is < 8 or > 1_048_576 ||
                iterations is < 1 or > 20 || parallelism is < 1 or > 32 || salt.Length is < 8 or > 64 ||
                hash.Length is < 16 or > 128) return false;
            phc = new(parts[1], memory, iterations, parallelism, salt, hash);
            return true;
        }
        catch { return false; }
    }

    private static char NormalizeWeakChar(char c) => char.ToLowerInvariant(c) switch
    {
        '@' or '4' => 'a', '$' or '5' => 's', '0' => 'o', '1' or '!' => 'i', '3' => 'e', '7' => 't', var x => x
    };
    private static bool IsAsciiPunctuation(char c) => c is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~';
    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');
    private static byte[] FromB64(string text) => Convert.FromBase64String(text.PadRight((text.Length + 3) / 4 * 4, '='));
    private readonly record struct Phc(string Algorithm, int MemoryKb, int Iterations, int Parallelism, byte[] Salt, byte[] Hash);
}
