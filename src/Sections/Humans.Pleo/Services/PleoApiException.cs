namespace Humans.Pleo.Services;

internal abstract class PleoApiException : Exception
{
    protected PleoApiException() { }
    protected PleoApiException(string message) : base(message) { }
    protected PleoApiException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>5xx, 429, network, timeout, or a page that cannot be trusted: may be retried.</summary>
internal sealed class PleoTransientException : PleoApiException
{
    public PleoTransientException() { }
    public PleoTransientException(string message) : base(message) { }
    public PleoTransientException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>4xx or an unreadable body: never retry. A 403 is Pleo's limit on the key, not a config problem.</summary>
internal sealed class PleoPermanentException : PleoApiException
{
    public int? StatusCode { get; }

    public PleoPermanentException() { }
    public PleoPermanentException(string message) : base(message) { }
    public PleoPermanentException(string message, Exception inner) : base(message, inner) { }
    public PleoPermanentException(string message, int statusCode) : base(message) => StatusCode = statusCode;
}
