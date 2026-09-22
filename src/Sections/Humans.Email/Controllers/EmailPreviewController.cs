using Humans.Base.Controllers;
using Humans.Email.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Humans.Email.Controllers;

/// <summary>
/// Backs the shared <c>_EmailComposer</c> component's "Preview" and "Send to me" buttons. Open to any
/// authenticated human — every compose form (profile message, camp contact, rota
/// messages, survey invitation, feedback reply, issue comment, campaign) needs it, not
/// just admins, unlike the rest of <see cref="EmailController"/>.
/// </summary>
[Authorize]
[Route("Email")]
internal sealed class EmailPreviewController(
    IUserServiceRead userService,
    IEmailPreviewService emailPreviews,
    ComposerSelfSendService selfSend) : ApiControllerBase(userService)
{
    /// <summary>Named rate-limit policy for <see cref="SendMarkdownToSelf"/>, registered in <c>Section</c>.</summary>
    public const string SelfSendRateLimitPolicy = "EmailComposerSelfSend";
    public const int SelfSendPermitsPerMinute = 5;

    [HttpPost("PreviewMarkdown", Name = "EmailPreviewMarkdown")]
    [ValidateAntiForgeryToken]
    public IActionResult PreviewMarkdown([FromForm] string? subject, [FromForm] string? body, [FromForm] string? category)
    {
        MessageCategory? parsedCategory = !string.IsNullOrWhiteSpace(category)
            && Enum.TryParse<MessageCategory>(category, out var value)
                ? value
                : null;

        var preview = emailPreviews.RenderMarkdown(subject ?? string.Empty, body, parsedCategory);
        return new JsonResult(new { subject = preview.Subject, html = preview.HtmlBody });
    }

    /// <summary>
    /// Queues the composer's current subject/body to the signed-in human's own address — never to
    /// anyone else — so they can see the real branded send in their inbox. Rate-limited per human.
    /// </summary>
    [HttpPost("SendMarkdownToSelf", Name = "EmailSendMarkdownToSelf")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(SelfSendRateLimitPolicy)]
    public async Task<IActionResult> SendMarkdownToSelf(
        [FromForm] string? subject, [FromForm] string? body, CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Unauthorized();

        var sentTo = await selfSend.SendToSelfAsync(userId.Value, subject, body, ct);
        if (sentTo is null) return UnprocessableEntity();

        return new JsonResult(new { email = sentTo });
    }
}
