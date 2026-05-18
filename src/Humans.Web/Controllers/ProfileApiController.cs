using Humans.Application.DTOs;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Profiles;
using Humans.Domain.Enums;
using Humans.Web.Extensions;
using Humans.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

[Authorize]
[ApiController]
[Route("api/profiles")]
public class ProfileApiController : ApiControllerBase
{
    private const int MaxResults = 10;

    private readonly IProfileService _profileService;
    private readonly IUserService _userService;
    private readonly IContactFieldService _contactFieldService;
    private readonly IUserEmailService _userEmailService;

    public ProfileApiController(
        IProfileService profileService,
        IUserService userService,
        IContactFieldService contactFieldService,
        IUserEmailService userEmailService)
        : base(userService)
    {
        _profileService = profileService;
        _userService = userService;
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

        // scope=name → display/burner name only; default → broad public match. Admin bit never set on public endpoint.
        var fields = string.Equals(scope, "name", StringComparison.OrdinalIgnoreCase)
            ? PersonSearchFields.Name
            : PersonSearchFields.PublicAll;

        // Cover the deleted-user-but-session-still-valid race — fail-closed with 401.
        var (authError, viewer) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (authError is not null)
            return authError;
        var viewerUserId = viewer.Id;

        var results = await _userService.SearchUsersAsync(q, fields, MaxResults, ct);

        var response = new List<HumanLookupSearchResult>(results.Count);
        foreach (var result in results.OrderBy(r => r.BurnerName, StringComparer.OrdinalIgnoreCase))
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

        // Display sort at controller — memory/architecture/display-sort-in-controllers.md.
        return Ok(response);
    }

    // Single-person lookup by userId — same shape as Search.
    [HttpGet("by-userid/{userId:guid}")]
    public async Task<IActionResult> GetByUserId(Guid userId, CancellationToken ct = default)
    {
        var (authError, viewer) = await ResolveCurrentUserOrUnauthorizedAsync();
        if (authError is not null)
            return authError;
        var viewerUserId = viewer.Id;

        var info = await _userService.GetUserInfoAsync(userId, ct);
        if (info?.Profile is null || info.Profile.RejectedAt is not null)
            return NotFound();

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

    // Disambiguation: viewer-visible primary email → highest-priority visible contact field → null. Legal name omitted deliberately.
    private async Task<string?> GetSharedDetailAsync(
        Guid userId,
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
            userId,
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
