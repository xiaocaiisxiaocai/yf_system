using MySqlConnector;

namespace Yf.Api.Infrastructure;

public sealed class AppOptions
{
    public string ConnectionString { get; set; } = "";
    public string StorageRoot { get; set; } = "";
    public string JwtSecret { get; set; } = "";
    public bool AutoInitializeDatabase { get; set; }
    public string BootstrapPassword { get; set; } = "";
    public string WebBaseUrl { get; set; } = "http://127.0.0.1:5273";
    public bool CookieSecure { get; set; } = true;
    /// <summary>
    /// Explicit opt-in for CookieSecure=false outside Development when WebBaseUrl is a non-loopback HTTP origin
    /// (private LAN deployments without TLS). Never allowed for an HTTPS WebBaseUrl.
    /// </summary>
    public bool AllowInsecureCookies { get; set; }
    public bool TrustLoopbackProxy { get; set; }
    /// <summary>
    /// Optional absolute directory for daily-rolling compact-JSON log files (outside the application
    /// directory). Empty keeps only the default logging providers.
    /// </summary>
    public string LogDirectory { get; set; } = "";
    public bool WorkerEnabled { get; set; } = true;
    /// <summary>Runs durable user-requested project copy jobs independently of maintenance workers.</summary>
    public bool CopyWorkerEnabled { get; set; } = true;
    public int AccessTtlMinutes { get; set; } = 30;
    public int RefreshTtlDays { get; set; } = 7;
    public int AbsoluteSessionLifetimeDays { get; set; } = 30;
    /// <summary>Maximum concurrently active login sessions per account; the oldest are revoked on a new login.</summary>
    public int MaxActiveSessionsPerUser { get; set; } = 20;
    public long UploadMaxFileSize { get; set; } = 2L * 1024 * 1024 * 1024; // matches the seeded upload.max_file_size
    public int UploadChunkSize { get; set; } = 10 * 1024 * 1024;
    /// <summary>Audit log rows older than this many days are deleted by the retention worker.</summary>
    public int AuditRetentionDays { get; set; } = 30;
    /// <summary>Sent, failed and cancelled outbox rows older than this many days are deleted by the mail worker.</summary>
    public int MailRetentionDays { get; set; } = 90;
    /// <summary>Outbox rows still PENDING this many days after they were queued are cancelled instead of sent.</summary>
    public int MailPendingTtlDays { get; set; } = 3;
    /// <summary>Terminal upload-session rows and their temporary directories are retained for recovery before deletion.</summary>
    public int UploadSessionRetentionDays { get; set; } = 90;
    /// <summary>Maximum unexpired UPLOADING/MERGING upload sessions one account may hold at once.</summary>
    public int UploadMaxActiveSessionsPerUser { get; set; } = 20;
    /// <summary>Maximum declared bytes of one account's unexpired, not yet merged upload sessions.</summary>
    public long UploadMaxPendingBytesPerUser { get; set; } = 20L * 1024 * 1024 * 1024;
    /// <summary>Maximum bytes the files maintenance worker re-hashes per cycle while verifying referenced blobs.</summary>
    public long BlobVerifyBytesPerCycle { get; set; } = 2L * 1024 * 1024 * 1024;
    /// <summary>A batch ZIP download that delivers fewer bytes than this within any 60-second window is aborted.</summary>
    public long BatchDownloadMinBytesPerMinute { get; set; } = 64 * 1024;
    /// <summary>A batch ZIP download still running after this many minutes is aborted.</summary>
    public int BatchDownloadMaxDurationMinutes { get; set; } = 120;
    public SmtpOptions Smtp { get; set; } = new();
    /// <summary>Independent local OEM content root, excluded from collaboration backups. Empty disables file transfer.</summary>
    public string OemStorageRoot { get; set; } = "";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) throw new InvalidOperationException("App:ConnectionString must be configured.");
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
        if (AccessTtlMinutes is < 1 or > 1440 || RefreshTtlDays is < 1 or > 365
            || AbsoluteSessionLifetimeDays is < 1 or > 365)
            throw new InvalidOperationException("Invalid token lifetime.");
        if (MaxActiveSessionsPerUser is < 1 or > 1000)
            throw new InvalidOperationException("App:MaxActiveSessionsPerUser must be between 1 and 1000.");
        if (AuditRetentionDays is < 1 or > 3650) throw new InvalidOperationException("App:AuditRetentionDays must be between 1 and 3650.");
        if (MailRetentionDays is < 1 or > 3650) throw new InvalidOperationException("App:MailRetentionDays must be between 1 and 3650.");
        if (MailPendingTtlDays is < 1 or > 365) throw new InvalidOperationException("App:MailPendingTtlDays must be between 1 and 365.");
        if (UploadSessionRetentionDays is < 30 or > 3650) throw new InvalidOperationException("App:UploadSessionRetentionDays must be between 30 and 3650.");
        if (UploadMaxActiveSessionsPerUser is < 1 or > 1000)
            throw new InvalidOperationException("App:UploadMaxActiveSessionsPerUser must be between 1 and 1000.");
        if (UploadMaxPendingBytesPerUser is < 1024L * 1024 * 1024 or > 16L * 1024 * 1024 * 1024 * 1024)
            throw new InvalidOperationException("App:UploadMaxPendingBytesPerUser must be between 1 GiB and 16 TiB.");
        if (BlobVerifyBytesPerCycle is < 64L * 1024 * 1024 or > 1024L * 1024 * 1024 * 1024)
            throw new InvalidOperationException("App:BlobVerifyBytesPerCycle must be between 64 MiB and 1 TiB.");
        if (BatchDownloadMinBytesPerMinute is < 1 or > 1024L * 1024 * 1024)
            throw new InvalidOperationException("App:BatchDownloadMinBytesPerMinute must be between 1 byte and 1 GiB.");
        if (BatchDownloadMaxDurationMinutes is < 5 or > 1440)
            throw new InvalidOperationException("App:BatchDownloadMaxDurationMinutes must be between 5 and 1440.");
        Smtp.Validate();
        if (OemStorageRoot.Length > 0)
        {
            if (!Path.IsPathFullyQualified(OemStorageRoot) || OemStorageRoot.StartsWith(@"\", StringComparison.Ordinal))
                throw new InvalidOperationException("App:OemStorageRoot must be an absolute local path.");
            if (Path.GetFullPath(OemStorageRoot).TrimEnd(Path.DirectorySeparatorChar) == Path.GetPathRoot(OemStorageRoot)?.TrimEnd(Path.DirectorySeparatorChar))
                throw new InvalidOperationException("App:OemStorageRoot cannot be a drive root.");
        }
    }

    /// <summary>
    /// Refuses non-secure auth cookies unless the site is a loopback/development setup or the operator
    /// explicitly opted in for a plain-HTTP private deployment. An HTTPS site always requires secure cookies.
    /// </summary>
    public void ValidateCookieSecurity(bool isDevelopment)
    {
        if (CookieSecure) return;
        if (!Uri.TryCreate(WebBaseUrl, UriKind.Absolute, out var web))
            throw new InvalidOperationException("App:WebBaseUrl must be an HTTP(S) origin.");
        if (web.Scheme == Uri.UriSchemeHttps)
            throw new InvalidOperationException("App:CookieSecure must be true when App:WebBaseUrl uses HTTPS.");
        if (isDevelopment || web.IsLoopback || AllowInsecureCookies) return;
        throw new InvalidOperationException(
            "App:CookieSecure=false is only allowed in Development, for a loopback App:WebBaseUrl, or with App:AllowInsecureCookies=true.");
    }

    public void ValidateStorageLocation(string applicationRoot)
    {
        if (!string.IsNullOrWhiteSpace(LogDirectory))
        {
            if (!Path.IsPathFullyQualified(LogDirectory))
                throw new InvalidOperationException("App:LogDirectory must be an absolute path.");
            if (Overlaps(ResolveComparisonPath(LogDirectory), ResolveComparisonPath(applicationRoot),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("App:LogDirectory must be separate from the application directory and its public web files.");
        }
        // Compare the resolved paths, not only their textual forms. A junction
        // or symlink used as StorageRoot must not be able to point back into
        // the application tree and bypass the isolation check.
        var app = ResolveComparisonPath(applicationRoot);
        var storage = ResolveComparisonPath(StorageRoot);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Overlaps(storage, app, comparison))
            throw new InvalidOperationException("StorageRoot must be separate from the application directory and its public web files.");
        if (OemStorageRoot.Length > 0)
        {
            var oem = ResolveComparisonPath(OemStorageRoot);
            if (Overlaps(oem, app, comparison) || Overlaps(oem, storage, comparison))
                throw new InvalidOperationException("OemStorageRoot must be separate from the application and collaboration storage directories.");
            if (!string.IsNullOrWhiteSpace(LogDirectory) && Overlaps(oem, ResolveComparisonPath(LogDirectory), comparison))
                throw new InvalidOperationException("OemStorageRoot must be separate from the log directory.");
        }
    }

    private static bool Overlaps(string left, string right, StringComparison comparison) =>
        left.Equals(right, comparison) || left.StartsWith(right + Path.DirectorySeparatorChar, comparison)
        || right.StartsWith(left + Path.DirectorySeparatorChar, comparison);

    private static string ResolveComparisonPath(string path, int remainingLinks = 64)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)
            ?? throw new InvalidOperationException("无法解析存储目录路径。");
        var resolved = root;
        // Resolve every existing component. Inspecting only the nearest existing
        // parent misses links further up when the leaf is an ordinary directory.
        foreach (var part in full[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, part);
            FileSystemInfo? entry = Directory.Exists(resolved) ? new DirectoryInfo(resolved)
                : File.Exists(resolved) ? new FileInfo(resolved) : null;
            if (entry is null || (entry.Attributes & FileAttributes.ReparsePoint) == 0) continue;
            if (remainingLinks <= 0) throw new InvalidOperationException("存储路径的链接层级过深。");
            var target = entry.ResolveLinkTarget(returnFinalTarget: true)
                ?? throw new InvalidOperationException("无法解析存储目录链接。");
            resolved = ResolveComparisonPath(target.FullName, remainingLinks - 1);
        }
        return Path.GetFullPath(resolved).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
