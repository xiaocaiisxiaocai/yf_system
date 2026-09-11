using MySqlConnector;

namespace Yf.Api.Infrastructure;

public sealed class AppOptions
{
    public string ConnectionString { get; set; } = "";
    public string StorageRoot { get; set; } = "";
    public string JwtSecret { get; set; } = "";
    public string WebBaseUrl { get; set; } = "http://127.0.0.1:5273";
    public bool CookieSecure { get; set; } = true;
    public bool TrustLoopbackProxy { get; set; }
    public bool WorkerEnabled { get; set; } = true;
    public int AccessTtlMinutes { get; set; } = 30;
    public int RefreshTtlDays { get; set; } = 7;
    public long UploadMaxFileSize { get; set; } = 20L * 1024 * 1024 * 1024;
    public int UploadChunkSize { get; set; } = 10 * 1024 * 1024;
    public SmtpOptions Smtp { get; set; } = new();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) throw new InvalidOperationException("App:ConnectionString must be configured; no database is created automatically.");
        try { _ = new MySqlConnectionStringBuilder(ConnectionString); }
        catch { throw new InvalidOperationException("Invalid App:ConnectionString."); }
        if (System.Text.Encoding.UTF8.GetByteCount(JwtSecret) < 32) throw new InvalidOperationException("App:JwtSecret must be at least 32 bytes.");
        if (string.IsNullOrWhiteSpace(StorageRoot) || !Path.IsPathFullyQualified(StorageRoot))
            throw new InvalidOperationException("App:StorageRoot must be an absolute path to the existing independent storage directory.");
        if (Path.GetFullPath(StorageRoot).TrimEnd(Path.DirectorySeparatorChar) == Path.GetPathRoot(StorageRoot)?.TrimEnd(Path.DirectorySeparatorChar))
            throw new InvalidOperationException("App:StorageRoot cannot be a drive root.");
        if (!Uri.TryCreate(WebBaseUrl, UriKind.Absolute, out var web) || web.Scheme is not ("http" or "https") || web.UserInfo.Length != 0)
            throw new InvalidOperationException("App:WebBaseUrl must be an HTTP(S) origin.");
        if (AccessTtlMinutes is < 1 or > 1440 || RefreshTtlDays is < 1 or > 365) throw new InvalidOperationException("Invalid token lifetime.");
        if (Smtp.Host.Length > 0 && (Smtp.Username.Length == 0 || Smtp.Password.Length == 0 || Smtp.From.Length == 0))
            throw new InvalidOperationException("SMTP requires username, password and from.");
    }

    public void ValidateStorageLocation(string applicationRoot)
    {
        var app = Path.GetFullPath(applicationRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var storage = Path.GetFullPath(StorageRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (storage.Equals(app, comparison) || storage.StartsWith(app + Path.DirectorySeparatorChar, comparison) || app.StartsWith(storage + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException("StorageRoot must be separate from the application directory and its public web files.");
    }
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 465;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";
}
