using Humans.Base.Controllers;
using Humans.Email.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Email.Controllers;

/// <summary>
/// Backs the shared <c>_EmailComposer</c> component's "Preview" button. Open to any
/// authenticated human — every compose form (profile message, camp contact, rota
/// messages, survey invitation, feedback reply, issue comment, campaign) needs it, not
/// just admins, unlike the rest of <see cref="EmailController"/>.
/// </summary>
[Authorize]
[Route("Email")]
internal sealed class EmailPreviewController(
    IUserServiceRead userService,
    IEmailPreviewService emailPreviews) : ApiControllerBase(userService)
{
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
}
