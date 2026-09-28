namespace Yf.Api.Modules.SystemManagement;

/// <summary>The <c>email_outbox.status</c> values.</summary>
internal static class MailStatuses
{
    /// <summary>Waiting for delivery (or for the next retry / the end of a coalescing window).</summary>
    public const string Pending = "PENDING";
    /// <summary>Claimed by a worker; <c>next_attempt_at</c> is the lease expiry.</summary>
    public const string Sending = "SENDING";
    public const string Sent = "SENT";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";
}
