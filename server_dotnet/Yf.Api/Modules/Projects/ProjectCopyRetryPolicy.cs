using MySqlConnector;
using Yf.Api.Infrastructure;

namespace Yf.Api.Modules.Projects;

/// <summary>
/// Decides whether a failed background copy job goes back to the queue. Only MySQL deadlocks (1213) and
/// lock-wait timeouts (1205) are retried: the whole copy transaction was rolled back, so running it again is
/// safe. Retries are bounded and back off exponentially; every other failure (and an exhausted budget) fails
/// the job as before.
/// </summary>
internal static class ProjectCopyRetryPolicy
{
    internal const uint MaximumRetries = 5;
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(60);

    /// <summary>The backoff before the next attempt, or null when the job must fail now.</summary>
    internal static TimeSpan? RetryDelay(Exception error, uint retriesSoFar) =>
        retriesSoFar < MaximumRetries && IsTransientLockFailure(error) ? Backoff(retriesSoFar) : null;

    internal static TimeSpan Backoff(uint retriesSoFar)
    {
        var delay = FirstDelay * Math.Pow(2, Math.Min(retriesSoFar, 16u));
        return delay < MaximumDelay ? delay : MaximumDelay;
    }

    internal static bool IsTransientLockFailure(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is MySqlException { Number: 1205 or 1213 }) return true;
            if (current is ApiException { Code: 40902 }) return true;
        }
        return false;
    }
}
