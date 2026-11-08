using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Identity;

public static class PasswordService
{
    public const int MinPasswordChars = 5;
    public const int MaxPasswordChars = 20;
    public const int MaxPasswordBytes = 256;
    public const string PolicyMessage = "密码需 5-20 个字符，不能使用常见弱密码或简单重复序列";
    public const string InitialPolicyMessage = "初始密码需为 5-20 个字符";
    private const int MemoryKb = 19_456;
    private const int Iterations = 2;
    private const int Parallelism = 1;
    private const int HashBytes = 32;
    // Each Argon2id derivation takes ~19 MiB and one core for ~0.1 s. Use half the cores (2-8) so login
    // bursts are not serialized, while memory stays bounded (at most 8 x 19 MiB).
    internal static readonly int HashConcurrency = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
    private static readonly SemaphoreSlim HashSlots = new(HashConcurrency);
    // A flood must not queue requests without bound; callers get a retryable busy response instead.
    internal static readonly TimeSpan HashQueueTimeout = TimeSpan.FromSeconds(15);
    private static readonly string[] BlockedStems =
    [
        "password", "passwd", "admin", "administrator", "qwerty", "qwertyuiop",
        "asdfghjkl", "letmein", "welcome", "changeme", "iloveyou", "monkey", "dragon",
        "football", "baseball", "abc", "abcd", "abcdef", "login", "test", "secret"
    ];
    private static readonly string[] SimpleSequenceRows =
    [
        "0123456789", "abcdefghijklmnopqrstuvwxyz", "qwertyuiop", "asdfghjkl", "zxcvbnm"
    ];
    private static readonly string[] SimpleSequenceWindows = SimpleSequenceRows
        .SelectMany(row => new[] { row, new string(row.Reverse().ToArray()) })
        .Select(row => string.Concat(Enumerable.Repeat(row, 3)))
        .ToArray();

    /// <summary>
    /// Initial passwords of batch-imported accounts are only length-checked (123456 is allowed): every new account
    /// must change its password at first login, and that change goes through <see cref="Validate"/>.
    /// The frontend mirror is <c>validateInitialPassword</c> in <c>web/src/utils/password.ts</c>.
    /// </summary>
    public static void ValidateInitial(string password)
    {
        if (!WithinLength(password)) throw ApiException.BadRequest(InitialPolicyMessage);
    }

    private static bool WithinLength(string? password) =>
        password is not null && password.EnumerateRunes().Count() is >= MinPasswordChars and <= MaxPasswordChars
        && Encoding.UTF8.GetByteCount(password) <= MaxPasswordBytes;

    public static void Validate(string password)
    {
        if (!StrongEnough(password))
            throw ApiException.BadRequest(PolicyMessage);
    }

    public static bool StrongEnough(string? password)
    {
        if (password is null) return false;
        var chars = password.EnumerateRunes().Count();
        if (chars is < MinPasswordChars or > MaxPasswordChars || Encoding.UTF8.GetByteCount(password) > MaxPasswordBytes) return false;

        var end = password.Length;
        while (end > 0 && password[end - 1] <= 127 && (char.IsDigit(password[end - 1]) || IsAsciiPunctuation(password[end - 1]))) end--;
        var core = password[..end];
        var stem = new string(core.Select(NormalizeWeakChar).Where(c => char.IsAsciiLetter(c)).ToArray());
        if (BlockedStems.Any(word => stem.Length > 0 && stem.Length % word.Length == 0 &&
                                     string.Concat(Enumerable.Repeat(word, stem.Length / word.Length)) == stem))
            return false;
        var sequenceEnd = password.Length;
        while (sequenceEnd > 0 && password[sequenceEnd - 1] <= 127 && IsAsciiPunctuation(password[sequenceEnd - 1])) sequenceEnd--;
        var sequenceCandidate = password[..sequenceEnd].ToLowerInvariant();
        if (IsSimpleSequence(stem) || IsSimpleSequence(sequenceCandidate))
            return false;
        var runes = password.EnumerateRunes().ToArray();
        return !Enumerable.Range(1, 4).Any(period => runes.Select((r, i) => (r, i)).All(x => x.r == runes[x.i % period]));
    }

    public static Task<string> HashAsync(string password, CancellationToken cancellationToken = default)
    {
        Validate(password);
        return HashUncheckedAsync(password, cancellationToken);
    }

    /// <summary>Hashes an import's initial password, which only has to pass <see cref="ValidateInitial"/>.</summary>
    public static Task<string> HashInitialAsync(string password, CancellationToken cancellationToken = default)
    {
        ValidateInitial(password);
        return HashUncheckedAsync(password, cancellationToken);
    }

    private static async Task<string> HashUncheckedAsync(string password, CancellationToken cancellationToken)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = await DeriveAsync("argon2id", password, salt, MemoryKb, Iterations, Parallelism, HashBytes,
            PasswordWorkload.Management, cancellationToken);
        return $"$argon2id$v=19$m={MemoryKb},t={Iterations},p={Parallelism}${B64(salt)}${B64(hash)}";
    }

    private static readonly Lazy<Task<string>> TimingDummy = new(CreateTimingDummyAsync);

    /// <summary>
    /// A fixed, same-cost Argon2id hash verified when an account does not exist, so
    /// unknown and known login names take indistinguishable time.
    /// </summary>
    internal static Task<string> TimingDummyHashAsync() => TimingDummy.Value;

    private static async Task<string> CreateTimingDummyAsync()
    {
        var salt = Encoding.ASCII.GetBytes("yf-login-dummy-salt");
        using var argon = new Konscious.Security.Cryptography.Argon2id(Encoding.UTF8.GetBytes("dummy-login#2026")) { Salt = salt, MemorySize = 19456, Iterations = 2, DegreeOfParallelism = 1 };
        return $"$argon2id$v=19$m=19456,t=2,p=1${Convert.ToBase64String(salt).TrimEnd('=')}${Convert.ToBase64String(await argon.GetBytesAsync(32)).TrimEnd('=')}";
    }

    public static async Task<bool> VerifyAsync(string password, string encodedHash, CancellationToken cancellationToken = default)
    {
        if (Encoding.UTF8.GetByteCount(password) > MaxPasswordBytes || !TryParse(encodedHash, out var phc)) return false;
        var actual = await DeriveAsync(phc.Algorithm, password, phc.Salt, phc.MemoryKb, phc.Iterations,
            phc.Parallelism, phc.Hash.Length, PasswordWorkload.Login, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(actual, phc.Hash);
    }

    internal static async Task<bool> VerifyForPasswordChangeAsync(
        string password, string encodedHash, CancellationToken cancellationToken = default)
    {
        if (Encoding.UTF8.GetByteCount(password) > MaxPasswordBytes || !TryParse(encodedHash, out var phc)) return false;
        var actual = await DeriveAsync(phc.Algorithm, password, phc.Salt, phc.MemoryKb, phc.Iterations,
            phc.Parallelism, phc.Hash.Length, PasswordWorkload.Management, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(actual, phc.Hash);
    }

    private static async Task<byte[]> DeriveAsync(string algorithm, string password, byte[] salt, int memoryKb,
        int iterations, int parallelism, int hashBytes, PasswordWorkload workload, CancellationToken cancellationToken)
    {
        if (!await HashSlots.WaitAsync(HashQueueTimeout, cancellationToken))
            throw workload == PasswordWorkload.Login
                ? LoginBusyException()
                : ApiException.PasswordRateLimited("密码处理繁忙，请稍后再试");
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            // Argon2 is a DeriveBytes: dispose it so its internal buffers are released, and wipe our
            // copy of the password bytes (the managed string itself cannot be wiped).
            using Argon2 argon = algorithm switch
            {
                "argon2id" => new Argon2id(passwordBytes),
                "argon2i" => new Argon2i(passwordBytes),
                "argon2d" => new Argon2d(passwordBytes),
                _ => throw new InvalidOperationException("Unsupported Argon2 algorithm")
            };
            argon.Salt = salt;
            argon.MemorySize = memoryKb;
            argon.Iterations = iterations;
            argon.DegreeOfParallelism = parallelism;
            return await argon.GetBytesAsync(hashBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            HashSlots.Release();
        }
    }

    internal static ApiException LoginBusyException() =>
        ApiException.TooManyRequests("登录请求繁忙，请稍后再试");

    private enum PasswordWorkload { Login, Management }

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
    private static bool IsSimpleSequence(string value)
    {
        if (value.Length < MinPasswordChars) return false;
        return SimpleSequenceWindows.Any(window => window.Contains(value, StringComparison.Ordinal));
    }
    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');
    private static byte[] FromB64(string text) => Convert.FromBase64String(text.PadRight((text.Length + 3) / 4 * 4, '='));
    private readonly record struct Phc(string Algorithm, int MemoryKb, int Iterations, int Parallelism, byte[] Salt, byte[] Hash);
}
