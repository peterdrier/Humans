using Humans.Base.Interfaces;

namespace Humans.Guide.Services;

/// <summary>
/// The section façade for retrieving rendered guide pages. Owns the memory cache.
/// </summary>
internal interface IGuideContentService : IApplicationService
{
    /// <summary>
    /// Returns the HTML for a guide file as this reader may see it: the cached segments are
    /// filtered by role first, and only what survives is rendered. Triggers a full refresh if
    /// the cache is cold. Throws <see cref="GuideContentUnavailableException"/> when GitHub is
    /// unreachable and no stale content is available.
    /// </summary>
    Task<string> GetPageAsync(
        string fileStem,
        GuideRoleContext roleContext,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-fetches and re-segments every known guide file from GitHub and overwrites its cache
    /// entry. Nothing is evicted first: a file that fails to fetch keeps the copy already
    /// cached, so a GitHub outage degrades to stale content rather than to an empty guide.
    /// </summary>
    Task RefreshAllAsync(CancellationToken cancellationToken = default);
}

internal sealed class GuideContentUnavailableException : Exception
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
