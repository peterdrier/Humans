namespace Humans.Application.Interfaces;

/// <summary>
/// Public façade for retrieving rendered guide pages. Owns the memory cache.
/// </summary>
public interface IGuideContentService
{
    /// <summary>
    /// Returns the rendered, role-annotated HTML for a guide file. Triggers a
    /// full refresh if the cache is cold. Throws <see cref="GuideContentUnavailableException"/>
    /// when GitHub is unreachable and no stale content is available.
    /// </summary>
    Task<string> GetRenderedAsync(string fileStem, CancellationToken cancellationToken = default);

    /// <summary>
    /// Evicts all cached entries and re-fetches every known guide file from GitHub.
    /// </summary>
    Task RefreshAllAsync(CancellationToken cancellationToken = default);
}

public sealed class GuideContentUnavailableException : Exception
{
    public GuideContentUnavailableException()
    {
    }

    public GuideContentUnavailableException(string message)
        : base(message)
    {
    }

    public GuideContentUnavailableException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
