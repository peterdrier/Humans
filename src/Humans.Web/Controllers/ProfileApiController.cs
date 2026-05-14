using Humans.Application.DTOs;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Services.Profiles;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Extensions;
using Humans.Web.Helpers;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

[Authorize]
[ApiController]
[Route("api/profiles")]
public class ProfileApiController : ApiControllerBase
{
    private const int MaxResults = 10;

    private readonly IProfileService _profileService;
    private readonly IContactFieldService _contactFieldService;
    private readonly IUserEmailService _userEmailService;

    public ProfileApiController(
        IProfileService profileService,
        IContactFieldService contactFieldService,
        IUserEmailService userEmailService,
        UserManager<User> userManager)
        : base(userManager)
    {
        _profileService = profileService;
        _contactFieldService = contactFieldService;
        _userEmailService = userEmailService;
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string? q,
        [FromQuery] string? scope = null,
        CancellationToken ct = default)
    {
        if (!q.HasSearchTerm())
            return Ok(Array.Empty<HumanLookupSearchResult>());

        // scope=name → narrowed match on display name + burner name only.
        // anything else (default) → broad match across bio / city / interests / CV +
        // every public ContactField. Admin bit is never set here — services are
        // auth-free, but a non-admin endpoint passing it would be a programmer
        // error caught in code review (see PersonSearchFields docs).
        var fields = string.Equals(scope, "name", StringComparison.OrdinalIgnoreCase)
            ? PersonSearchFields.Name
            : PersonSearchFields.PublicAll;

        // [Authorize] handles the no-cookie case at the framework layer;
        // resolve here to cover the deleted-user-but-session-still-valid race
        // — fail-closed with 401 rather than soft-fail into empty details.
        var (authError, viewer) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (authError is not null)
            return authError;
        var viewerUserId = viewer.Id;

        var results = await _profileService.SearchProfilesAsync(q, fields, MaxResults, ct);
        var userIds = results.Select(r => r.UserId).ToList();
        var pictureUrls = await ProfilePictureUrlHelper.BuildEffectiveUrlsAsync(
            _profileService,
            Url,
            userIds,
            ct);

        var response = new List<HumanLookupSearchResult>(results.Count);
        foreach (var result in results.OrderBy(r => r.BurnerName, StringComparer.OrdinalIgnoreCase))
        {
            var detail = await GetSharedDetailAsync(
                result.UserId,
                result.ProfileId,
                viewerUserId,
                ct);

            response.Add(new HumanLookupSearchResult(
                result.UserId,
                result.BurnerName,
                detail,
                pictureUrls.GetValueOrDefault(result.UserId)));
        }

        // Display ordering belongs at the presentation layer, per
        // memory/architecture/display-sort-in-controllers.md.
        return Ok(response);
    }

    /// <summary>
    /// Single-person lookup by userId. Returns the same picker row shape as
    /// <see cref="Search"/>, so callers that already know the userId (URL
    /// param, integration, pre-fill) can render the row without typing a name
    /// and choosing from a dropdown. Cache-backed via
    /// <see cref="IProfileService.GetFullProfileAsync"/>.
    /// </summary>
    [HttpGet("by-userid/{userId:guid}")]
    public async Task<IActionResult> GetByUserId(Guid userId, CancellationToken ct = default)
    {
        var (authError, viewer) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (authError is not null)
            return authError;
        var viewerUserId = viewer.Id;

        var fullProfile = await _profileService.GetFullProfileAsync(userId, ct);
        if (fullProfile is null || fullProfile.IsRejected)
            return NotFound();

        var pictureUrls = await ProfilePictureUrlHelper.BuildEffectiveUrlsAsync(
            _profileService,
            Url,
            new[] { userId },
            ct);

        var detail = await GetSharedDetailAsync(
            userId,
            fullProfile.ProfileId,
            viewerUserId,
            ct);

        var displayName = string.IsNullOrWhiteSpace(fullProfile.BurnerName)
            ? fullProfile.DisplayName
            : fullProfile.BurnerName!;

        return Ok(new HumanLookupSearchResult(
            userId,
            displayName,
            detail,
            pictureUrls.GetValueOrDefault(userId)));
    }

    /// <summary>
    /// Returns the disambiguation detail to render under the human's name in
    /// the shared picker row. Priority: viewer-visible primary email →
    /// highest-priority visible contact field (Phone → Signal → Telegram →
    /// WhatsApp → Discord → Other) → <c>null</c>. Legal name is deliberately
    /// not surfaced here even for board viewers: at ~500 users with mostly
    /// non-board callers it would be a distraction that almost no one
    /// benefits from, and board members can still see legal name via the
    /// profile card on click-through.
    /// </summary>
    private async Task<string?> GetSharedDetailAsync(
        Guid userId,
        Guid profileId,
        Guid viewerUserId,
        CancellationToken ct)
    {
        var accessLevel = await _contactFieldService.GetViewerAccessLevelAsync(
            userId,
            viewerUserId,
            ct);
        var visibleEmails = await _userEmailService.GetVisibleEmailsAsync(userId, accessLevel, ct);
        var visibleEmail = visibleEmails
            .OrderByDescending(e => e.IsPrimary)
            .ThenBy(e => e.Email, StringComparer.OrdinalIgnoreCase)
            .Select(e => e.Email)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(visibleEmail))
            return visibleEmail;

        var visibleContactFields = await _contactFieldService.GetVisibleContactFieldsAsync(
            profileId,
            viewerUserId,
            ct);

        return visibleContactFields
#pragma warning disable CS0618 // Obsolete ContactFieldType.Email is skipped; UserEmail is the canonical email source.
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
