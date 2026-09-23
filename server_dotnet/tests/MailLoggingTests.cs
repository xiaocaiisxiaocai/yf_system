using Microsoft.Extensions.Logging;
using Yf.Api.Modules.SystemManagement;

namespace Yf.Api.Tests;

public sealed class MailLoggingTests
{
    [Fact]
    public void WorkerFailureLogKeepsCallStackWithoutRawExceptionOrSensitiveMessages()
    {
        var logger = new RecordingLogger();
        Exception failure;
        try
        {
            ThrowSensitiveFailure();
            throw new InvalidOperationException("unreachable");
        }
        catch (Exception error)
        {
            failure = error;
        }

        MailService.LogWorkerFailure(logger, failure);

        var entry = Assert.Single(logger.Entries);
        Assert.Null(entry.Exception);
        Assert.Contains(nameof(ThrowSensitiveFailure), entry.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(InvalidOperationException).FullName!, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("smtp-password-123", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("bearer-token-456", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("person@example.invalid", entry.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void ThrowSensitiveFailure() =>
        throw new InvalidOperationException(
            "password=smtp-password-123 token=bearer-token-456 recipient=person@example.invalid",
            new IOException("inner secret smtp-password-123"));

    private sealed class RecordingLogger : ILogger
    {
        public List<(Exception? Exception, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((exception, formatter(state, exception)));
    }
}
