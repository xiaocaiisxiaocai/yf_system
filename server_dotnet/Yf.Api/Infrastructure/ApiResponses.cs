namespace Yf.Api.Infrastructure;

/// <summary>The <c>{}</c> body returned by commands that have nothing else to report.</summary>
public sealed record EmptyResponse
{
    public static readonly EmptyResponse Instance = new();
}

/// <summary>One page of a list endpoint.</summary>
public sealed record PageResponse<T>(IReadOnlyList<T> List, ulong Total, ulong Page, ulong PageSize);
