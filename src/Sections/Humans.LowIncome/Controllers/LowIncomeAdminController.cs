using Humans.Base.Authorization;
using Humans.Base.Controllers;
using Humans.Grants.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.LowIncome.Controllers;

/// <summary>
/// Grants does the work (row, audit, the person's notice) under the open program whose source key is
/// <see cref="Section.SourceKey"/>.
/// </summary>
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("LowIncome/Admin")]
internal sealed class LowIncomeAdminController(
    IGrantWrite grants,
    IUserService users,
    ILogger<LowIncomeAdminController> logger) : HumansControllerBase(users)
{
    private const string NoProgram =
        "There is no open Grants program for Low Income. Create one under Grants → Programs, issued by another section with source key \"" +
        Section.SourceKey + "\".";

    [HttpGet("")]
    public IActionResult Index() => View();

    [HttpPost("Grant")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Grant(Guid? userId, CancellationToken ct) =>
        ActAsync(nameof(Grant), userId, async (person, actor) =>
        {
            await grants.IssueAsync(Section.SourceKey, person, sourceRef: null, actor, ct);
            return (true, "Low Income grant given");
        }, ct);

    [HttpPost("Remove")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Remove(Guid? userId, string? reason, CancellationToken ct) =>
        ActAsync(nameof(Remove), userId, async (person, actor) =>
            await grants.RevokeAsync(Section.SourceKey, person, reason, actor, ct)
                ? (true, "Low Income grant removed")
                : (false, "That person holds no Low Income grant."), ct);

    private async Task<IActionResult> ActAsync(
        string action, Guid? userId, Func<Guid, Guid, Task<(bool Ok, string Message)>> act, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        if (userId is not { } person)
        {
            SetError("Pick a person first.");
            return RedirectToAction(nameof(Index));
        }

        try
        {
            var (ok, message) = await act(person, user.Id);
            if (ok) SetSuccess(message); else SetError(message);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Low Income {Action} refused for {UserId}", action, person);
            SetError(NoProgram);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Low Income {Action} rejected for {UserId}", action, person);
            SetError("The reason is too long.");
        }
        return RedirectToAction(nameof(Index));
    }
}
