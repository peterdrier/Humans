namespace Humans.Application.Interfaces.Holded;

public abstract class HoldedApiException : Exception
{
    protected HoldedApiException() { }
    protected HoldedApiException(string message) : base(message) { }
    protected HoldedApiException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Transient — eligible for retry (5xx, network, timeout).
/// </summary>
public sealed class HoldedTransientException : HoldedApiException
{
    public HoldedTransientException() { }
    public HoldedTransientException(string message) : base(message) { }
    public HoldedTransientException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Permanent — 4xx; do not retry.
/// </summary>
public sealed class HoldedPermanentException : HoldedApiException
{
    public int StatusCode { get; }
    public string? ResponseBody { get; }

    public HoldedPermanentException() { }

    public HoldedPermanentException(string message) : base(message) { }

    public HoldedPermanentException(string message, Exception inner) : base(message, inner) { }

    public HoldedPermanentException(int statusCode, string? body, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = body;
    }
}
