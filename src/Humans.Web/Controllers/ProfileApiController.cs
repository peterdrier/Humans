using Humans.Application.DTOs;
using Humans.Application.Interfaces.Auth;
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
public class ProfileApiController : ControllerBase
{
    private const int MaxResults = 10;

    private readonly IProfileService _profileService;
    private readonly IContactFieldService _contactFieldService;
    private readonly IUserEmailService _userEmailService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly UserManager<User> _userManager;

    public ProfileApiController(
        IProfileService profileService,
        IContactFieldService contactFieldService,
        IUserEmailService userEmailService,
        IRoleAssignmentService roleAssignmentService,
        UserManager<User> userManager)
    {
        _profileService = profileService;
        _contactFieldService = contactFieldService;
        _userEmailService = userEmailService;
        _roleAssignmentService = roleAssignmentService;
        _userManager = userManager;
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

        var results = await _profileService.SearchProfilesAsync(q, fields, MaxResults, ct);
        var userIds = results.Select(r => r.UserId).ToList();
        var profilesByUserId = await _profileService.GetByUserIdsAsync(userIds, ct);
        var pictureUrls = await ProfilePictureUrlHelper.BuildEffectiveUrlsAsync(
            _profileService,
            Url,
            userIds,
            ct);

        var viewer = await _userManager.GetUserAsync(User);
        var viewerUserId = viewer?.Id;
        var viewerIsBoard = viewerUserId.HasValue
            && await _roleAssignmentService.IsUserBoardMemberAsync(viewerUserId.Value, ct);

        var response = new List<HumanLookupSearchResult>(results.Count);
        foreach (var result in results.OrderBy(r => r.BurnerName, StringComparer.OrdinalIgnoreCase))
        {
            var detail = await GetSharedDetailAsync(
                result.UserId,
                profilesByUserId.GetValueOrDefault(result.UserId),
                viewerUserId,
                viewerIsBoard,
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

    private async Task<string?> GetSharedDetailAsync(
        Guid userId,
        Profile? profile,
        Guid? viewerUserId,
        bool viewerIsBoard,
        CancellationToken ct)
    {
        if (viewerUserId is null)
            return null;

        if (profile is not null && (viewerUserId.Value == userId || viewerIsBoard))
        {
            var legalName = profile.FullName;
            if (!string.IsNullOrWhiteSpace(legalName))
                return legalName;
        }

        var accessLevel = await _contactFieldService.GetViewerAccessLevelAsync(
            userId,
            viewerUserId.Value,
            ct);
        var visibleEmails = await _userEmailService.GetVisibleEmailsAsync(userId, accessLevel, ct);
        var visibleEmail = visibleEmails
            .OrderByDescending(e => e.IsPrimary)
            .ThenBy(e => e.Email, StringComparer.OrdinalIgnoreCase)
            .Select(e => e.Email)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(visibleEmail))
            return visibleEmail;

        if (profile is null)
            return null;

        var visibleContactFields = await _contactFieldService.GetVisibleContactFieldsAsync(
            profile.Id,
            viewerUserId.Value,
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
