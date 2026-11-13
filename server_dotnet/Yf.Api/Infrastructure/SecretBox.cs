using System.Security.Cryptography;
using System.Text;

namespace Yf.Api.Infrastructure;

/// <summary>
/// Encrypts stored credentials (SMTP authorization code, IPMS password) with AES-GCM under a key derived from
/// <c>App:JwtSecret</c> and a per-purpose label, so an upgrade or restore must keep the site's original secret.
/// Envelope: <c>v1.</c> + base64(nonce[12] + tag[16] + ciphertext).
/// </summary>
public static class SecretBox
{
    public static string Protect(string purpose, string appSecret, string value)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(value);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Key(purpose, appSecret), 16);
        aes.Encrypt(nonce, plaintext, cipher, tag);
        return "v1." + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    /// <summary>Throws <see cref="CryptographicException"/> when the envelope is malformed or the key no longer matches.</summary>
    public static string Unprotect(string purpose, string appSecret, string value)
    {
        if (!value.StartsWith("v1.", StringComparison.Ordinal)) throw new CryptographicException("Invalid credential envelope.");
        byte[] data;
        try { data = Convert.FromBase64String(value[3..]); }
        catch (FormatException error) { throw new CryptographicException("Invalid credential envelope.", error); }
        if (data.Length < 28) throw new CryptographicException("Invalid credential envelope.");
        var plain = new byte[data.Length - 28];
        using var aes = new AesGcm(Key(purpose, appSecret), 16);
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain);
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] Key(string purpose, string appSecret) => SHA256.HashData(Encoding.UTF8.GetBytes(purpose + "\0" + appSecret));
}
