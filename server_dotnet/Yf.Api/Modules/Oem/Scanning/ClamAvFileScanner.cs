using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Oem.Scanning;

/// <summary>
/// Streams quarantined content to a loopback-only clamd endpoint. A clean verdict is accepted
/// only after the bytes sent match the upload size/SHA-256 and a stable, parseable VERSION is
/// observed immediately before and after the scan.
/// </summary>
public sealed class ClamAvFileScanner : IFileScanner
{
    private static readonly byte[] VersionCommand = "zVERSION\0"u8.ToArray();
    private static readonly byte[] InStreamCommand = "zINSTREAM\0"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly OemClamAvOptions options;
    private readonly IPAddress address;
    private readonly TimeProvider time;
    private readonly ILogger logger;

    public ClamAvFileScanner(OemClamAvOptions options, TimeProvider? time = null, ILogger? logger = null)
    {
        options.Validate();
        this.options = options;
        address = IPAddress.Parse(options.Host);
        this.time = time ?? TimeProvider.System;
        this.logger = logger ?? NullLogger.Instance;
    }

    public string Name => "ClamAV";
    public bool SupportsSignatureFreshness => true;

    public async Task<ScanResult> ScanAsync(ScanTarget target, CancellationToken ct)
    {
        if (target.Size > (ulong)options.MaxStreamBytes)
            return Unscannable(null, $"文件超过 ClamAV 流式扫描上限 {options.MaxStreamBytes} 字节");
        if (target.Freshness is { } configuredFreshness && configuredFreshness.MaximumSignatureAge <= TimeSpan.Zero)
            return Error(null, "病毒库最大允许年龄必须大于零");

        ClamVersion before;
        try { before = await ReadVersionAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (IsEngineFailure(error)) { return Unavailable(error); }
        if (CheckFreshness(before, target.Freshness) is { } beforeFreshnessFailure) return beforeFreshnessFailure;

        ScanReply reply;
        try { reply = await StreamAsync(target, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (FileNotFoundException) { return Error(before, "待扫描文件不存在"); }
        catch (DirectoryNotFoundException) { return Error(before, "待扫描文件不存在"); }
        catch (UnauthorizedAccessException) { return Error(before, "待扫描文件无法读取"); }
        catch (Exception error) when (IsEngineFailure(error)) { return Unavailable(error); }

        if (reply.LocalFailure is not null) return Error(before, reply.LocalFailure);

        ClamVersion after;
        try { after = await ReadVersionAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (IsEngineFailure(error)) { return Unavailable(error); }

        if (before != after)
            return Error(after, "扫描期间 ClamAV 引擎或病毒库版本发生变化，请重试");

        if (CheckFreshness(after, target.Freshness) is { } afterFreshnessFailure) return afterFreshnessFailure;
        if (reply.LimitOrEncrypted is not null) return Unscannable(after, reply.LimitOrEncrypted);

        return reply.Kind switch
        {
            ScanReplyKind.Clean => Result(ScanVerdict.Clean, after, null, null),
            ScanReplyKind.Infected => Result(ScanVerdict.Infected, after, reply.ThreatName, null),
            ScanReplyKind.Error => Result(ScanVerdict.Error, after, null, reply.Error),
            _ => Error(after, "ClamAV 返回未知扫描结果"),
        };
    }

    private async Task<ClamVersion> ReadVersionAsync(CancellationToken ct)
    {
        using var client = await ConnectAsync(ct);
        await using var stream = client.GetStream();
        await stream.WriteAsync(VersionCommand, ct);
        var response = await ReadRecordAsync(stream, ct);
        return ParseVersion(response);
    }

    private async Task<ScanReply> StreamAsync(ScanTarget target, CancellationToken ct)
    {
        await using var file = new FileStream(target.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var client = await ConnectAsync(ct);
        await using var network = client.GetStream();
        await network.WriteAsync(InStreamCommand, ct);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        var header = new byte[4];
        ulong sent = 0;
        while (true)
        {
            var read = await file.ReadAsync(buffer, ct);
            if (read == 0) break;
            if (sent + (ulong)read > (ulong)options.MaxStreamBytes)
                return ScanReply.Unscannable($"文件超过 ClamAV 流式扫描上限 {options.MaxStreamBytes} 字节");
            sent += (ulong)read;
            hash.AppendData(buffer, 0, read);
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)read);
            await network.WriteAsync(header, ct);
            await network.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        await network.WriteAsync(new byte[4], ct);

        var response = await ReadRecordAsync(network, ct);
        var actualHash = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (sent != target.Size)
            return ScanReply.Failed($"扫描内容大小与上传记录不一致（记录 {target.Size}，实际 {sent}）");
        if (!actualHash.Equals(target.Sha256, StringComparison.OrdinalIgnoreCase))
            return ScanReply.Failed("扫描内容 SHA-256 与上传记录不一致");
        return ParseScanReply(response);
    }

    private async Task<TcpClient> ConnectAsync(CancellationToken ct)
    {
        var client = new TcpClient(address.AddressFamily);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.ConnectTimeoutSeconds));
        try
        {
            await client.ConnectAsync(address, options.Port, timeout.Token);
            return client;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException("连接本地 clamd 超时");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<string> ReadRecordAsync(Stream stream, CancellationToken ct)
    {
        const int maximumLength = 4096;
        using var content = new MemoryStream();
        var buffer = new byte[512];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;
            if (content.Length + read > maximumLength) throw new InvalidDataException("clamd 响应过长");
            content.Write(buffer, 0, read);
        }
        var bytes = content.ToArray();
        if (bytes.Length < 2 || bytes[^1] != 0)
            throw new InvalidDataException("clamd 响应在记录结束前被截断");
        if (bytes.AsSpan(0, bytes.Length - 1).Contains((byte)0))
            throw new InvalidDataException("clamd 返回了多余响应记录");
        return StrictUtf8.GetString(bytes, 0, bytes.Length - 1);
    }

    internal static ClamVersion ParseVersion(string response)
    {
        const string prefix = "ClamAV ";
        if (!response.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("clamd VERSION 响应格式无效");
        var fields = response[prefix.Length..].Split('/', 3);
        if (fields.Length != 3 || !IsVersionToken(fields[0])
            || !uint.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var signatureVersion)
            || signatureVersion == 0)
            throw new InvalidDataException("clamd VERSION 缺少有效引擎或病毒库版本");

        if (!DateTime.TryParseExact(fields[2], "ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out var localDate))
            throw new InvalidDataException("clamd VERSION 病毒库时间格式无效");
        localDate = DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified);
        var localZone = TimeZoneInfo.Local;
        if (localZone.IsInvalidTime(localDate))
            throw new InvalidDataException("clamd VERSION 病毒库时间落在无效本地时间");
        DateTimeOffset updatedAt;
        if (localZone.IsAmbiguousTime(localDate))
        {
            // The earlier UTC instant gives the older age and therefore the stricter freshness result.
            var offset = localZone.GetAmbiguousTimeOffsets(localDate).Max();
            updatedAt = new DateTimeOffset(localDate, offset).ToUniversalTime();
        }
        else
        {
            updatedAt = new DateTimeOffset(localDate, localZone.GetUtcOffset(localDate)).ToUniversalTime();
        }
        return new ClamVersion(fields[0], fields[1], updatedAt);
    }

    private static bool IsVersionToken(string value) => value.Length is > 0 and <= 64
        && value.All(character => !char.IsWhiteSpace(character) && !char.IsControl(character) && character != '/');

    private static ScanReply ParseScanReply(string response)
    {
        const string prefix = "stream: ";
        if (!response.StartsWith(prefix, StringComparison.Ordinal))
        {
            if (response.EndsWith(" ERROR", StringComparison.Ordinal)
                && response.Contains("size limit exceeded", StringComparison.OrdinalIgnoreCase))
                return ScanReply.Unscannable($"ClamAV 无法完整扫描：{response[..^" ERROR".Length]}");
            return ScanReply.Failed("clamd 扫描响应格式无效");
        }
        var verdict = response[prefix.Length..];
        if (verdict == "OK") return ScanReply.Clean();
        if (verdict.EndsWith(" FOUND", StringComparison.Ordinal))
        {
            var threat = verdict[..^" FOUND".Length];
            if (!IsSafeThreatName(threat)) return ScanReply.Failed("clamd FOUND 响应的威胁名称无效");
            if (threat.Contains("Encrypted", StringComparison.OrdinalIgnoreCase)
                || threat.StartsWith("Heuristics.Limits.Exceeded.", StringComparison.OrdinalIgnoreCase))
                return ScanReply.Unscannable($"ClamAV 无法完整检查内容：{threat}");
            return ScanReply.Infected(threat);
        }
        if (verdict.EndsWith(" ERROR", StringComparison.Ordinal))
        {
            var error = verdict[..^" ERROR".Length];
            if (error.Contains("size limit exceeded", StringComparison.OrdinalIgnoreCase)
                || error.Contains("encrypted", StringComparison.OrdinalIgnoreCase))
                return ScanReply.Unscannable($"ClamAV 无法完整扫描：{error}");
            return ScanReply.EngineError(error.Length == 0 ? "ClamAV 扫描失败" : error);
        }
        return ScanReply.Failed("clamd 返回未知扫描结果");
    }

    private ScanResult? CheckFreshness(ClamVersion version, ScanFreshnessPolicy? policy)
    {
        var now = time.GetUtcNow();
        if (version.SignatureUpdatedAt > now.AddMinutes(5))
            return Error(version, "ClamAV 病毒库时间晚于本机时间");
        if (policy is null || now - version.SignatureUpdatedAt <= policy.MaximumSignatureAge) return null;
        return ScanResult.Unavailable(Name, $"ClamAV 病毒库已超过允许年龄 {policy.MaximumSignatureAge}") with
        {
            EngineVersion = version.EngineVersion,
            SignatureVersion = version.SignatureVersion,
            SignatureUpdatedAt = version.SignatureUpdatedAt,
        };
    }

    private static bool IsSafeThreatName(string value) => value.Length is > 0 and <= 255
        && value.All(character => !char.IsControl(character));

    private static bool IsEngineFailure(Exception error) => error is SocketException or IOException or TimeoutException or InvalidDataException
        or DecoderFallbackException;

    private ScanResult Unavailable(Exception error)
    {
        logger.LogWarning("Local clamd scan request failed ({ErrorType}); file remains quarantined.", error.GetType().Name);
        return ScanResult.Unavailable(Name, $"本地 ClamAV 服务不可用：{error.Message}");
    }

    private ScanResult Error(ClamVersion? version, string reason) => Result(ScanVerdict.Error, version, null, reason);
    private ScanResult Unscannable(ClamVersion? version, string reason) => Result(ScanVerdict.Unscannable, version, null, reason);
    private ScanResult Result(ScanVerdict verdict, ClamVersion? version, string? threat, string? error) =>
        new(verdict, Name, version?.EngineVersion, version?.SignatureVersion, threat, error, version?.SignatureUpdatedAt);

    internal sealed record ClamVersion(string EngineVersion, string SignatureVersion, DateTimeOffset SignatureUpdatedAt);
    private enum ScanReplyKind { Clean, Infected, Error }
    private sealed record ScanReply(ScanReplyKind Kind, string? ThreatName, string? Error, string? LocalFailure, string? LimitOrEncrypted)
    {
        public static ScanReply Clean() => new(ScanReplyKind.Clean, null, null, null, null);
        public static ScanReply Infected(string threat) => new(ScanReplyKind.Infected, threat, null, null, null);
        public static ScanReply EngineError(string error) => new(ScanReplyKind.Error, null, error, null, null);
        public static ScanReply Failed(string error) => new(ScanReplyKind.Error, null, null, error, null);
        public static ScanReply Unscannable(string error) => new(ScanReplyKind.Error, null, null, null, error);
    }
}
