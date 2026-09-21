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

    /// <summary>
    /// Local directory holding OEM file content. Deliberately separate from
    /// <see cref="StorageRoot"/>: it is never part of any backup. Empty disables OEM file features.
    /// </summary>
    public string OemStorageRoot { get; set; } = "";
    public OemScannerOptions OemScanner { get; set; } = new();

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
        if (OemStorageRoot.Length > 0)
        {
            if (!Path.IsPathFullyQualified(OemStorageRoot))
                throw new InvalidOperationException("App:OemStorageRoot must be an absolute local path.");
            if (Path.GetFullPath(OemStorageRoot).TrimEnd(Path.DirectorySeparatorChar) == Path.GetPathRoot(OemStorageRoot)?.TrimEnd(Path.DirectorySeparatorChar))
                throw new InvalidOperationException("App:OemStorageRoot cannot be a drive root.");
            if (OemStorageRoot.StartsWith(@"\", StringComparison.Ordinal))
                throw new InvalidOperationException("App:OemStorageRoot must be a local directory, not a network share.");
        }
        OemScanner.Validate();
    }

    public void ValidateStorageLocation(string applicationRoot)
    {
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
            // OEM content is never backed up, so it must not live inside (or contain)
            // the backed-up collaboration storage or the application tree.
            var oem = ResolveComparisonPath(OemStorageRoot);
            if (Overlaps(oem, app, comparison))
                throw new InvalidOperationException("OemStorageRoot must be separate from the application directory.");
            if (Overlaps(oem, storage, comparison))
                throw new InvalidOperationException("OemStorageRoot must be separate from StorageRoot (OEM files are excluded from backups).");
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

/// <summary>OEM malware scanner selection. Until an engine is configured nothing is released.</summary>
public sealed class OemScannerOptions
{
    /// <summary>
    /// None (fail closed: files stay unavailable), ClamAV (a local clamd service), OnAccess
    /// (the server's endpoint antivirus through real-time scanning) or Fake (development/testing only).
    /// </summary>
    public string Engine { get; set; } = "None";
    public OemClamAvOptions ClamAv { get; set; } = new();
    public OemOnAccessOptions OnAccess { get; set; } = new();
    /// <summary>Must be true to run the Fake engine, so a test configuration cannot reach production by accident.</summary>
    public bool AcknowledgeInsecureFake { get; set; }
    public int BaseTimeoutSeconds { get; set; } = 120;
    public int TimeoutSecondsPerGb { get; set; } = 300;

    public void Validate()
    {
        if (Engine is not ("None" or "ClamAV" or "OnAccess" or "Fake")) throw new InvalidOperationException("App:OemScanner:Engine must be None, ClamAV, OnAccess or Fake.");
        if (Engine == "ClamAV") ClamAv.Validate();
        if (Engine == "OnAccess") OnAccess.Validate();
        if (Engine == "Fake" && !AcknowledgeInsecureFake)
            throw new InvalidOperationException("The Fake OEM scanner performs no malware scanning; set App:OemScanner:AcknowledgeInsecureFake=true only for development or tests.");
        if (BaseTimeoutSeconds is < 5 or > 86400 || TimeoutSecondsPerGb is < 0 or > 86400)
            throw new InvalidOperationException("Invalid OEM scanner timeouts.");
    }
}

/// <summary>Local clamd INSTREAM integration. TCP is deliberately restricted to loopback.</summary>
public sealed class OemClamAvOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3310;
    public int ConnectTimeoutSeconds { get; set; } = 5;
    /// <summary>
    /// Maximum file size submitted to clamd. ClamAV 1.4 supports at most 2 GiB - 1;
    /// this must also match clamd's StreamMaxLength/MaxFileSize deployment settings.
    /// </summary>
    public long MaxStreamBytes { get; set; } = 1024L * 1024 * 1024;

    public void Validate()
    {
        if (!System.Net.IPAddress.TryParse(Host, out var address) || !System.Net.IPAddress.IsLoopback(address))
            throw new InvalidOperationException("App:OemScanner:ClamAv:Host must be a numeric loopback address (127.0.0.1 or ::1).");
        if (Port is < 1 or > 65535)
            throw new InvalidOperationException("App:OemScanner:ClamAv:Port must be between 1 and 65535.");
        if (ConnectTimeoutSeconds is < 1 or > 60)
            throw new InvalidOperationException("App:OemScanner:ClamAv:ConnectTimeoutSeconds must be 1-60.");
        if (MaxStreamBytes is < 1 or > 2_147_483_647)
            throw new InvalidOperationException("App:OemScanner:ClamAv:MaxStreamBytes must be 1-2147483647.");
    }
}

/// <summary>Real-time (on-access) antivirus integration; see OnAccessFileScanner.</summary>
public sealed class OemOnAccessOptions
{
    /// <summary>Shown in scan results and audit, e.g. "OfficeScan".</summary>
    public string ProductName { get; set; } = "OfficeScan";
    /// <summary>Wait after a file was written (and again after it was read) before judging it.</summary>
    public int SettleSeconds { get; set; } = 5;
    /// <summary>How long the antivirus has to intercept the EICAR canary.</summary>
    public int CanaryTimeoutSeconds { get; set; } = 60;
    /// <summary>How long a successful canary check is trusted.</summary>
    public int CanaryIntervalMinutes { get; set; } = 60;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProductName) || ProductName.Length > 64) throw new InvalidOperationException("App:OemScanner:OnAccess:ProductName must be 1-64 characters.");
        if (SettleSeconds is < 0 or > 120) throw new InvalidOperationException("App:OemScanner:OnAccess:SettleSeconds must be 0-120.");
        if (CanaryTimeoutSeconds is < 5 or > 600) throw new InvalidOperationException("App:OemScanner:OnAccess:CanaryTimeoutSeconds must be 5-600.");
        if (CanaryIntervalMinutes is < 5 or > 1440) throw new InvalidOperationException("App:OemScanner:OnAccess:CanaryIntervalMinutes must be 5-1440.");
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
