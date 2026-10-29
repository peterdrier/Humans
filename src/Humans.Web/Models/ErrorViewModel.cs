namespace Humans.Web.Models;

/// <summary>The generic error page.</summary>
/// <param name="StatusCode">The HTTP status sent with the page.</param>
/// <param name="RequestId">Matches the <c>RequestId</c> on the request's log entries.</param>
/// <param name="ExceptionDetail">Null unless the viewer may see it (QA, or Board/Admin).</param>
public sealed record ErrorViewModel(int StatusCode, string RequestId, string? ExceptionDetail);
