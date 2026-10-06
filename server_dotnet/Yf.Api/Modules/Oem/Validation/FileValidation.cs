using System.Security.Cryptography;
using Yf.Api.Modules.Oem.Policies;

namespace Yf.Api.Modules.Oem.Validation;

/// <summary>Public file-validation state. The legacy database column is retained only for storage compatibility.</summary>
public static class ValidationStatuses
{
    public const string Pending = "PENDING";
    public const string Validating = "VALIDATING";
    public const string Valid = "VALID";
    public const string Invalid = "INVALID";
    public const string Error = "ERROR";

    public static bool IsKnown(string status) => status is Pending or Validating or Valid or Invalid or Error;
}

/// <summary>Worker state persisted in the legacy scan-job table.</summary>
public static class ValidationJobStatuses
{
    public const string Pending = "PENDING";
    public const string Running = "RUNNING";
    public const string Valid = "VALID";
    public const string Invalid = "INVALID";
    public const string Error = "ERROR";
}

public enum ValidationVerdict { Valid, Invalid, Error }

public sealed record ValidationResult(ValidationVerdict Verdict, string? Message)
{
    public static readonly ValidationResult Valid = new(ValidationVerdict.Valid, null);
    public static ValidationResult Invalid(string message) => new(ValidationVerdict.Invalid, message);
    public static ValidationResult Error(string message) => new(ValidationVerdict.Error, message);
}

/// <summary>The quarantined file and the immutable size/SHA-256 recorded when upload completed.</summary>
public sealed record ValidationTarget(string Path, string Sha256, ulong Size);

/// <summary>
/// File-content validation pipeline. It verifies byte length and SHA-256, checks that
/// the content signature agrees with the extension, and enforces archive structure and
/// expansion limits. It does not perform or claim malware detection.
/// </summary>
public sealed class OemFileValidationPipeline
{
    public async Task<ValidationResult> RunAsync(
        ValidationTarget target,
        string extension,
        ArchiveLimits limits,
        string workDirectory,
        CancellationToken ct)
    {
        var integrity = await ValidateIntegrityAsync(target, ct);
        if (integrity is not null) return ValidationResult.Invalid(integrity);

        var signature = await FileSignatureInspector.InspectFileAsync(target.Path, extension, ct);
        if (!signature.Accepted)
            return ValidationResult.Invalid(signature.Reason ?? "文件内容与扩展名不符");

        ArchiveVerdict archive;
        try { archive = await ArchiveInspector.InspectAsync(target.Path, extension, limits, workDirectory, ct); }
        catch (InvalidDataException) { archive = new ArchiveVerdict(ArchiveOutcome.Corrupt, "压缩包内容无效"); }
        catch (Exception error) when (IsContentFault(error))
        {
            // Archive readers report malformed structures with arbitrary runtime exceptions
            // (IOException, InvalidOperationException, ArgumentException...). The bytes were
            // just read in full and matched the recorded SHA-256, so the content is the cause
            // and the verdict is final.
            archive = new ArchiveVerdict(ArchiveOutcome.Corrupt, "压缩包已损坏或格式无效");
        }

        return archive.Outcome is ArchiveOutcome.Encrypted or ArchiveOutcome.LimitExceeded or ArchiveOutcome.Corrupt
            or ArchiveOutcome.Forbidden
            ? ValidationResult.Invalid(archive.Reason ?? "压缩包内容无效")
            : ValidationResult.Valid;
    }

    internal static bool IsContentFault(Exception error) =>
        error is not (OperationCanceledException or ArchiveWorkspaceException or OutOfMemoryException);

    private static async Task<string?> ValidateIntegrityAsync(ValidationTarget target, CancellationToken ct)
    {
        var info = new FileInfo(target.Path);
        if (!info.Exists) return "隔离文件不存在";
        if ((ulong)info.Length != target.Size) return "文件大小与上传记录不一致";

        await using var stream = new FileStream(target.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        return actual.Equals(target.Sha256, StringComparison.OrdinalIgnoreCase)
            ? null
            : "文件 SHA-256 与上传记录不一致";
    }
}
