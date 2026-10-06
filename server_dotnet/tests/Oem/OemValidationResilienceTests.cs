using System.Security.Cryptography;
using System.Text;
using Yf.Api.Modules.Oem.Policies;
using Yf.Api.Modules.Oem.Validation;

namespace Yf.Api.Tests.Oem;

/// <summary>Malformed archives must always end in a final verdict, never an exception that strands the job.</summary>
public sealed class OemValidationResilienceTests
{
    private static readonly ArchiveLimits Limits = new(100, 3, 10 * 1024 * 1024, 100);

    public static TheoryData<string> Formats => new() { "zip", "7z", "rar" };

    [Theory(Timeout = 120_000)]
    [MemberData(nameof(Formats))]
    public async Task MutatedArchivesAlwaysProduceAVerdict(string extension)
    {
        var ct = TestContext.Current.CancellationToken;
        var work = Directory.CreateTempSubdirectory("oem-fuzz-").FullName;
        try
        {
            var seed = Seed(extension);
            var pipeline = new OemFileValidationPipeline();
            var random = new Random(20261005);
            for (var round = 0; round < 300; round++)
            {
                var bytes = Mutate(seed, random, round);
                if (bytes.Length == 0) continue;
                var path = Path.Combine(work, $"m{round}.{extension}");
                await File.WriteAllBytesAsync(path, bytes, ct);
                var target = new ValidationTarget(path, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), (ulong)bytes.Length);
                var result = await pipeline.RunAsync(target, extension, Limits, work, ct);
                Assert.NotEqual(ValidationVerdict.Error, result.Verdict);
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void OnlyCancellationAndWorkDirectoryFailuresStayRetryable()
    {
        Assert.True(OemFileValidationPipeline.IsContentFault(new ArgumentOutOfRangeException()));
        Assert.True(OemFileValidationPipeline.IsContentFault(new InvalidOperationException()));
        Assert.True(OemFileValidationPipeline.IsContentFault(new IOException("Zip file corrupt")));
        Assert.False(OemFileValidationPipeline.IsContentFault(new ArchiveWorkspaceException(new IOException("disk full"))));
        Assert.False(OemFileValidationPipeline.IsContentFault(new OperationCanceledException()));
    }

    private const string TinyRar = "UmFyIRoHAQAzkrXlCgEFBgAFAQGAgAAkmeyhIgICjAAGjAC2gwLQDlA6/o/BboAAAQx0ZXN0ZmlsZS50eHRUZXN0aW5nIDEyMwodd1ZRAwUEAA==";

    public static TheoryData<string, string> HiddenArchives => new()
    {
        { "stl", "zip-after-byte" },
        { "pdf", "zip-appended-to-pdf" },
        { "stl", "rar-after-stub" },
    };

    [Theory]
    [MemberData(nameof(HiddenArchives))]
    public async Task ArchivesHiddenBehindOtherContentAreRefused(string extension, string shape)
    {
        byte[] bytes = shape switch
        {
            "zip-after-byte" => [0x00, .. OemInspectionTests.EncryptedZip()],
            "zip-appended-to-pdf" => [.. OemTestHost.Pdf("cover"), .. OemInspectionTests.Zip(("a.txt", "x"u8.ToArray()))],
            "rar-after-stub" => [.. "solid-stub"u8.ToArray(), .. Convert.FromBase64String(TinyRar)],
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        var result = await RunAsync(bytes, extension);
        Assert.Equal(ValidationVerdict.Invalid, result.Verdict);
        Assert.Contains("夹带了压缩包", result.Message!);
    }

    [Fact]
    public async Task OrdinaryFilesWithoutHiddenArchivesStayValid()
    {
        Assert.Equal(ValidationVerdict.Valid, (await RunAsync(OemTestHost.Pdf("plain drawing"), "pdf")).Verdict);
        var binary = new byte[256 * 1024];
        new Random(7).NextBytes(binary);
        Assert.Equal(ValidationVerdict.Valid, (await RunAsync(binary, "stl")).Verdict);
    }

    [Fact]
    public async Task ExecutablesAndUnsafePathsInsideArchivesAreRefused()
    {
        byte[] executable = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00];
        var nested = await RunAsync(OemInspectionTests.Zip(("tools/setup.dat", executable)), "zip");
        Assert.Equal(ValidationVerdict.Invalid, nested.Verdict);
        Assert.Contains("可执行程序", nested.Message!);

        foreach (var name in new[] { "../escape.txt", "a/../../escape.txt", "C:/Windows/escape.txt", "/etc/escape.txt", "dir\\..\\escape.txt" })
        {
            var result = await RunAsync(OemInspectionTests.Zip((name, "x"u8.ToArray())), "zip");
            Assert.Equal(ValidationVerdict.Invalid, result.Verdict);
            Assert.Contains("路径不安全", result.Message!);
        }
        Assert.Equal(ValidationVerdict.Valid, (await RunAsync(OemInspectionTests.Zip(("dir/..name.txt", "x"u8.ToArray())), "zip")).Verdict);
    }

    [Fact]
    public void ZipDirectoryCountStopsAtTheLimitBeforeTheArchiveIsOpened()
    {
        var work = Directory.CreateTempSubdirectory("oem-zipdir-").FullName;
        try
        {
            var path = Path.Combine(work, "many.zip");
            File.WriteAllBytes(path, OemInspectionTests.Zip(Enumerable.Range(0, 150).Select(i => ($"f{i}.txt", "x"u8.ToArray())).ToArray()));
            Assert.Equal(101, ArchiveInspector.ZipDirectory.CountRecords(path, 100));
            Assert.Equal(150, ArchiveInspector.ZipDirectory.CountRecords(path, 1000));
            var truncated = File.ReadAllBytes(path);
            File.WriteAllBytes(path, truncated[..^30]);
            Assert.Null(ArchiveInspector.ZipDirectory.CountRecords(path, 1000));
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    private static async Task<ValidationResult> RunAsync(byte[] bytes, string extension)
    {
        var ct = TestContext.Current.CancellationToken;
        var work = Directory.CreateTempSubdirectory("oem-hardening-").FullName;
        try
        {
            var path = Path.Combine(work, "input." + extension);
            await File.WriteAllBytesAsync(path, bytes, ct);
            var target = new ValidationTarget(path, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), (ulong)bytes.Length);
            return await new OemFileValidationPipeline().RunAsync(target, extension, Limits, work, ct);
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    private static byte[] Seed(string extension) => extension switch
    {
        "zip" => OemInspectionTests.Zip(("a.txt", "hello zip"u8.ToArray()), ("b/c.txt", Encoding.ASCII.GetBytes(new string('x', 600)))),
        "7z" => OemInspectionTests.SevenZip(("a.txt", "hello 7z"u8.ToArray()), ("b.txt", Encoding.ASCII.GetBytes(new string('y', 600)))),
        // Minimal RAR5 fixture from ssokolow/rar-test-files, CC0-1.0 (see OemInspectionTests).
        "rar" => Convert.FromBase64String(TinyRar),
        _ => throw new ArgumentOutOfRangeException(nameof(extension)),
    };

    /// <summary>Alternates truncation and random byte corruption; the leading signature is kept so the archive reader runs.</summary>
    private static byte[] Mutate(byte[] seed, Random random, int round)
    {
        const int keep = 8;
        if (round % 3 == 0) return seed[..random.Next(keep, seed.Length)];
        var bytes = seed.ToArray();
        var flips = 1 + random.Next(4);
        for (var i = 0; i < flips; i++) bytes[random.Next(keep, bytes.Length)] = (byte)random.Next(256);
        return bytes;
    }
}
