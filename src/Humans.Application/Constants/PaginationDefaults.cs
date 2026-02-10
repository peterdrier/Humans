namespace Humans.Application.Constants;

/// <summary>
/// Default values for pagination and display limits.
/// </summary>
public static class PaginationDefaults
{
    /// <summary>
    /// Default page size for lists.
    /// </summary>
    public const int PageSize = 20;

    /// <summary>
    /// Maximum characters to show in motivation preview.
    /// </summary>
    public const int MaxMotivationPreview = 100;

    /// <summary>
    /// Maximum characters to store for user agent strings.
    /// </summary>
    public const int MaxUserAgentLength = 500;

    /// <summary>
    /// Number of consent history items to show.
    /// </summary>
    public const int ConsentHistoryLimit = 10;
}
