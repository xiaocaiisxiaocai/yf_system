using System.Reflection;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Yf.Api.Infrastructure;
using Yf.Api.Modules.Projects;

namespace Yf.Api.Tests;

public sealed class ProjectCopyRetryPolicyTests
{
    [Theory]
    [InlineData(MySqlErrorCode.LockDeadlock)]
    [InlineData(MySqlErrorCode.LockWaitTimeout)]
    public void DeadlocksAndLockWaitTimeoutsGoBackToTheQueueWithBoundedBackoff(MySqlErrorCode code)
    {
        var direct = MySql(code);
        Exception[] shapes =
        [
            direct,
            new DbUpdateException("save failed", direct),
            new InvalidOperationException("outer", new DbUpdateException("save failed", direct)),
            ApiException.Busy(),
        ];
        foreach (var error in shapes)
        {
            Assert.True(ProjectCopyRetryPolicy.IsTransientLockFailure(error));
            Assert.Equal(TimeSpan.FromSeconds(2), ProjectCopyRetryPolicy.RetryDelay(error, 0));
            Assert.Equal(TimeSpan.FromSeconds(32), ProjectCopyRetryPolicy.RetryDelay(error, 4));
            // The retry budget is bounded: the sixth failure fails the job.
            Assert.Null(ProjectCopyRetryPolicy.RetryDelay(error, ProjectCopyRetryPolicy.MaximumRetries));
            Assert.Null(ProjectCopyRetryPolicy.RetryDelay(error, uint.MaxValue));
        }
    }

    [Fact]
    public void EveryOtherFailureFailsTheJobImmediately()
    {
        Exception[] errors =
        [
            MySql(MySqlErrorCode.DuplicateKeyEntry),
            new DbUpdateException("save failed", MySql(MySqlErrorCode.NoReferencedRow2)),
            ApiException.Conflict("版本冲突"),
            ApiException.BadRequest("参数错误"),
            new IOException("disk full"),
            new InvalidOperationException("broken"),
            new OperationCanceledException(),
        ];
        foreach (var error in errors)
        {
            Assert.False(ProjectCopyRetryPolicy.IsTransientLockFailure(error));
            Assert.Null(ProjectCopyRetryPolicy.RetryDelay(error, 0));
        }
    }

    [Fact]
    public void BackoffDoublesFromTwoSecondsAndIsCappedAtOneMinute()
    {
        Assert.Equal(
            [2, 4, 8, 16, 32, 60, 60],
            new uint[] { 0, 1, 2, 3, 4, 5, 6 }.Select(n => (int)ProjectCopyRetryPolicy.Backoff(n).TotalSeconds).ToArray());
        Assert.Equal(TimeSpan.FromSeconds(60), ProjectCopyRetryPolicy.Backoff(uint.MaxValue));
    }

    private static MySqlException MySql(MySqlErrorCode code)
    {
        // MySqlException has no public constructor; build one the way the driver does.
        var constructor = typeof(MySqlException)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(c => c.GetParameters().FirstOrDefault()?.ParameterType == typeof(MySqlErrorCode))
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var arguments = constructor.GetParameters()
            .Select((p, index) => index == 0 ? code
                : p.ParameterType == typeof(string) ? (object?)(p.Name == "sqlState" ? "40001" : $"MySQL error {(int)code}")
                : null)
            .ToArray();
        var error = (MySqlException)constructor.Invoke(arguments);
        Assert.Equal((int)code, error.Number);
        return error;
    }
}
