using Humans.Base.Interfaces;

namespace Humans.Guide.Services;

internal interface IGuideContentService : IApplicationService
{
    /// <summary>
    /// Filters the cached segments by role, then renders what survives. Throws
    /// <see cref="GuideContentUnavailableException"/> when this file has no cached copy and its fetch fails, or when
    /// rendering fails, so the reader gets the 503 view instead of a 500.
    /// </summary>
    Task<string> GetPageAsync(
        string fileStem,
        GuideRoleContext roleContext,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-fetches every guide file and overwrites its cache entry. Nothing is evicted first: a failed fetch keeps the
    /// cached copy, so a GitHub outage serves stale content, not an empty guide.
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
