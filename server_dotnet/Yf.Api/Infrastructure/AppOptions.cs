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
        MySqlConnectionStringBuilder database;
        try { database = new MySqlConnectionStringBuilder(ConnectionString); }
        catch { throw new InvalidOperationException("Invalid App:ConnectionString."); }
        DatabaseTransportPolicy.Validate(database);
        if (System.Text.Encoding.UTF8.GetByteCount(JwtSecret) < 32) throw new InvalidOperationException("App:JwtSecret must be at least 32 bytes.");
        if (string.IsNullOrWhiteSpace(StorageRoot) || !Path.IsPathFullyQualified(StorageRoot))
            throw new InvalidOperationException("App:StorageRoot must be an absolute path to the existing independent storage directory.");
        if (Path.GetFullPath(StorageRoot).TrimEnd(Path.DirectorySeparatorChar) == Path.GetPathRoot(StorageRoot)?.TrimEnd(Path.DirectorySeparatorChar))
            throw new InvalidOperationException("App:StorageRoot cannot be a drive root.");
        if (!Uri.TryCreate(WebBaseUrl, UriKind.Absolute, out var web)
            || web.Scheme is not ("http" or "https")
            || web.UserInfo.Length != 0
            || web.AbsolutePath != "/"
            || web.Query.Length != 0
            || web.Fragment.Length != 0)
            throw new InvalidOperationException("App:WebBaseUrl must be an HTTP(S) origin.");
        if (AccessTtlMinutes is < 1 or > 1440 || RefreshTtlDays is < 1 or > 365) throw new InvalidOperationException("Invalid token lifetime.");
        Smtp.Validate();
    }

    public void ValidateStorageLocation(string applicationRoot)
    {
        // Compare the resolved paths, not only their textual forms. A junction
        // or symlink used as StorageRoot must not be able to point back into
        // the application tree and bypass the isolation check.
        var app = ResolveComparisonPath(applicationRoot);
        var storage = ResolveComparisonPath(StorageRoot);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (storage.Equals(app, comparison) || storage.StartsWith(app + Path.DirectorySeparatorChar, comparison) || app.StartsWith(storage + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException("StorageRoot must be separate from the application directory and its public web files.");
    }

    private static string ResolveComparisonPath(string path)
    {
        var full = Path.GetFullPath(path);
        var missing = new Stack<string>();
        var existing = full;
        while (!Directory.Exists(existing) && !File.Exists(existing))
        {
            var parent = Directory.GetParent(existing)
                ?? throw new InvalidOperationException("无法解析存储目录路径。");
            missing.Push(Path.GetFileName(existing));
            existing = parent.FullName;
        }

        if (Directory.Exists(existing))
        {
            var resolved = new DirectoryInfo(existing).ResolveLinkTarget(returnFinalTarget: true);
            if (resolved is not null) existing = resolved.FullName;
        }

        while (missing.Count > 0) existing = Path.Combine(existing, missing.Pop());
        return Path.GetFullPath(existing).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 465;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";
    public string Security { get; set; } = "Auto";

    public bool IsConfigured => Host.Length > 0;

    public void Validate()
    {
        // An empty host deliberately disables SMTP. Any attempted configuration
        // must be complete so the application does not start with a dead queue.
        if (!IsConfigured) return;
        if (string.IsNullOrWhiteSpace(Host))
            throw new InvalidOperationException("App:Smtp:Host cannot be whitespace.");
        if (Port is < 1 or > 65535)
            throw new InvalidOperationException("App:Smtp:Port must be between 1 and 65535.");
        if (Security is not ("Auto" or "SslOnConnect" or "StartTls"))
            throw new InvalidOperationException("SMTP security must be Auto, SslOnConnect or StartTls.");
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
            throw new InvalidOperationException("SMTP requires a non-empty username and password.");
        if (string.IsNullOrWhiteSpace(From) || !System.Net.Mail.MailAddress.TryCreate(From, out _))
            throw new InvalidOperationException("App:Smtp:From must be a valid email address.");
    }
}
