namespace Humans.Holded.Contracts;

public abstract class HoldedApiException : Exception
{
    protected HoldedApiException() { }
    protected HoldedApiException(string message) : base(message) { }
    protected HoldedApiException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>5xx, network or timeout: may be retried.</summary>
public sealed class HoldedTransientException : HoldedApiException
{
    public HoldedTransientException() { }
    public HoldedTransientException(string message) : base(message) { }
    public HoldedTransientException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>4xx: never retry.</summary>
public sealed class HoldedPermanentException : HoldedApiException
{
    public int StatusCode { get; }
    public string? ResponseBody { get; }

    /// <summary>The one bank movement a read failed closed on, so a caller can tell a persistently stuck line
    /// from a fresh failure.</summary>
    public string? BankMovementId { get; }

    public HoldedPermanentException() { }

    public HoldedPermanentException(string message) : base(message) { }

    public HoldedPermanentException(string message, Exception inner) : base(message, inner) { }

    public HoldedPermanentException(string message, Exception inner, string? bankMovementId)
        : base(message, inner)
    {
        BankMovementId = bankMovementId;
    }

    public HoldedPermanentException(int statusCode, string? body, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = body;
    }
}
