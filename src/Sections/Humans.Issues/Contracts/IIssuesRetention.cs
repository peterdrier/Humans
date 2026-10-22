namespace Humans.Issues.Contracts;

public interface IIssuesRetention
{
    /// <summary>
    /// Deletes issues terminal for at least 6 months, with their screenshot files. Returns rows removed.
    /// </summary>
    Task<int> PurgeExpiredAsync(CancellationToken ct = default);
}
