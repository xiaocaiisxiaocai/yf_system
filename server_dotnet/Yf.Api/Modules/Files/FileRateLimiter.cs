using System.Collections.Concurrent;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Files;

/// <summary>
/// Per-account budget of collaboration upload and download entry points (fixed one-minute window,
/// in memory: single instance only). Starting a file upload, posting a message with images and
/// starting a download (single, batch, or their native-download grants) are counted separately so
/// one cannot starve another. Chunks, resume and merge of a started upload, redeeming a download
/// grant, previews and media streaming are bounded elsewhere and are not counted.
/// </summary>
public sealed class FileRateLimiter(AppOptions options)
{
    public const string FileUploadScope = "file-upload";
    public const string MessageImageScope = "message-image";
    public const string DownloadScope = "file-download";

    private const int MaximumWindows = 10_000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<(string Scope, ulong UserId), (DateTime Start, int Count)> windows = new();

    /// <summary>Counts one call; 429/42902 once the account has spent this minute's budget for <paramref name="scope"/>.</summary>
    public void Require(ulong userId, string scope)
    {
        if (!Allow(userId, scope, DateTime.UtcNow))
            throw ApiException.TooManyRequests(scope == DownloadScope ? "下载过于频繁，请稍后再试" : "上传过于频繁，请稍后再试");
    }

    internal bool Allow(ulong userId, string scope, DateTime now)
    {
        if (windows.Count > MaximumWindows)
            foreach (var stale in windows.Where(item => now - item.Value.Start >= Window).ToArray())
                windows.TryRemove(stale.Key, out _);
        var window = windows.AddOrUpdate((scope, userId), _ => (now, 1),
            (_, old) => now - old.Start >= Window ? (now, 1) : (old.Start, old.Count + 1));
        return window.Count <= options.FileTransferStartsPerMinutePerUser;
    }
}
