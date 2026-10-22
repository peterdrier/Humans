using Humans.Base.Controllers;
using Humans.Users.Contracts;
using Humans.Base.Extensions;
using Humans.Base.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;


namespace Humans.Users.Controllers;

[Authorize]
[ApiController]
[Route("api/profiles")]
internal sealed class ProfileApiController(
    IUserService userService,
    IContactFieldService contactFieldService,
    IUserEmailService userEmailService) : ApiControllerBase(userService)
{
    // Uncapped: a cap returned an arbitrary N in cache order (the person searched for could be missing), then
    // alphabetized them. Rank the full match set by relevance instead.
    private const int Unlimited = int.MaxValue;

    private readonly IUserService _userService = userService;

    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string? q,
        [FromQuery] string? scope = null,
        [FromQuery] bool allowEmail = false,
        CancellationToken ct = default)
    {
        if (!q.HasSearchTerm())
            return Ok(Array.Empty<HumanLookupSearchResult>());

        // Never the Admin bit on this public endpoint.
        var fields = string.Equals(scope, "name", StringComparison.OrdinalIgnoreCase)
            ? PersonSearchFields.Name
            : PersonSearchFields.PublicAll;

        // A deleted user with a still-valid session fails closed with 401.
        var (authError, viewer) = await ResolveCurrentUserOrUnauthorizedAsync(ct);
        if (authError is not null)
            return authError;
        var viewerUserId = viewer.Id;

        // An exact verified-email match resolves at most one person: no enumeration or substring leak, so it is safe on
        // this non-admin endpoint.
        if (allowEmail && q.Contains('@', StringComparison.Ordinal))
        {
            var owners = (await userEmailService.FindByAddressAsync(q, aliased: false, verifiedOnly: true, ct))
                .Select(r => r.UserId).Distinct().ToList();
            if (owners.Count != 1)
                return Ok(Array.Empty<HumanLookupSearchResult>());
            Guid? matchedUserId = owners[0];

            var matchedInfo = await _userService.GetUserInfoAsync(matchedUserId.Value, ct);
            if (matchedInfo is not { IsSearchable: true })
                return Ok(Array.Empty<HumanLookupSearchResult>());

            var emailDetail = await GetSharedDetailAsync(matchedUserId.Value, viewerUserId, ct);
            return Ok(new[]
            {
                new HumanLookupSearchResult(
                    matchedUserId.Value, matchedInfo.BurnerName, emailDetail, matchedInfo.ProfilePictureUrl)
            });
        }

        var results = await _userService.SearchUsersAsync(q, fields, Unlimited, ct);

        var response = new List<HumanLookupSearchResult>(results.Count);
        foreach (var result in results.OrderByRelevance())
        {
            var detail = await GetSharedDetailAsync(
                result.UserId,
                viewerUserId,
                ct);

            response.Add(new HumanLookupSearchResult(
                result.UserId,
                result.BurnerName,
                detail,
                result.ProfilePictureUrl));
        }

        return Ok(response);
    }

    // Uncapped: the real number must surface. Excludes the editing user.
    [HttpGet("burner-name-count")]
    public async Task<IActionResult> BurnerNameCount(
        [FromQuery] string? name,
        CancellationToken ct = default)
    {
        if (!name.HasSearchTerm())
            return Ok(new BurnerNameCountResult(0));

        // The session identity, never a caller-supplied id, so the self-exclusion can't be spoofed.
        var (authError, viewer) = await ResolveCurrentUserOrUnauthorizedAsync(ct);
        if (authError is not null)
            return authError;

        var matches = await _userService.SearchUsersAsync(name, PersonSearchFields.ExactName, int.MaxValue, ct);
        var count = matches.Count(r => r.UserId != viewer.Id);
        return Ok(new BurnerNameCountResult(count));
    }

    [HttpGet("by-userid/{userId:guid}")]
    public async Task<IActionResult> GetByUserId(Guid userId, CancellationToken ct = default)
    {
        var (authError, viewer) = await ResolveCurrentUserOrUnauthorizedAsync(ct);
        if (authError is not null)
            return authError;
        var viewerUserId = viewer.Id;

        var info = await _userService.GetUserInfoAsync(userId, ct);
        if (info is not { IsSearchable: true })
            return NotFound();
        userId = info.Id; // a merged-away id answers as its survivor

        var detail = await GetSharedDetailAsync(
            userId,
            viewerUserId,
            ct);

        return Ok(new HumanLookupSearchResult(
            userId,
            info.BurnerName,
            detail,
            info.ProfilePictureUrl));
    }

    // Visible primary email, else the highest-priority visible contact field. Never the legal name.
    private async Task<string?> GetSharedDetailAsync(
        Guid userId,
        Guid viewerUserId,
        CancellationToken ct)
    {
        var accessLevel = await contactFieldService.GetViewerAccessLevelAsync(
            userId,
            viewerUserId,
            ct);
        var visibleEmails = await userEmailService.GetVisibleEmailsAsync(userId, accessLevel, ct);
        var visibleEmail = visibleEmails
            .OrderByDescending(e => e.IsPrimary)
            .ThenBy(e => e.Email, StringComparer.OrdinalIgnoreCase)
            .Select(e => e.Email)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(visibleEmail))
            return visibleEmail;

        var visibleContactFields = await contactFieldService.GetVisibleContactFieldsAsync(
            userId,
            viewerUserId,
            ct);

        return visibleContactFields
#pragma warning disable CS0618 // UserEmail is the email source; the obsolete Email type is skipped.
            .Where(f => f.FieldType is not ContactFieldType.Email)
#pragma warning restore CS0618
            .OrderBy(f => GetContactFieldDisplayPriority(f.FieldType))
            .ThenBy(f => f.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Value, StringComparer.OrdinalIgnoreCase)
            .Select(FormatContactFieldDetail)
            .FirstOrDefault();
    }

    private static int GetContactFieldDisplayPriority(ContactFieldType fieldType) =>
        fieldType switch
        {
            ContactFieldType.Phone => 0,
            ContactFieldType.Signal => 1,
            ContactFieldType.Telegram => 2,
            ContactFieldType.WhatsApp => 3,
            ContactFieldType.Discord => 4,
            ContactFieldType.Other => 5,
            _ => 99
        };

    private static string FormatContactFieldDetail(ContactFieldDto field)
    {
        var label = string.IsNullOrWhiteSpace(field.Label)
            ? field.FieldType.ToString()
            : field.Label;

        return $"{label} {field.Value}";
    }
}
