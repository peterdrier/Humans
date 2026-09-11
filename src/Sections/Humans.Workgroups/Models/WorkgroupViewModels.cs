using System.ComponentModel.DataAnnotations;
using Humans.Users.Contracts;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Services;
using NodaTime;

namespace Humans.Workgroups.Models;

/// <summary>
/// The register (design §15): the three lists the page shows, plus the clock the badges are
/// measured against. Badges themselves are not baked in — the views ask
/// <see cref="WorkgroupRhythm"/>, so page and job can never disagree.
/// </summary>
internal sealed class RegisterViewModel
{
    public required Instant Now { get; init; }

    public required IReadOnlyList<WorkgroupInfo> Active { get; init; }

    /// <summary>Applied and Referred: waiting on the Secretary or the Board.</summary>
    public required IReadOnlyList<WorkgroupInfo> Pending { get; init; }

    /// <summary>Dormant, Withdrawn and Refused, each with its reason on the row.</summary>
    public required IReadOnlyList<WorkgroupInfo> Archive { get; init; }

    /// <summary>Burner names for every coordinator on the page, by user id.</summary>
    public required IReadOnlyDictionary<Guid, UserInfo> People { get; init; }

    public required Guid CurrentUserId { get; init; }

    public static RegisterViewModel Build(
        IReadOnlyList<WorkgroupInfo> register,
        IReadOnlyDictionary<Guid, UserInfo> people,
        Guid currentUserId,
        Instant now) => new()
        {
            Now = now,
            CurrentUserId = currentUserId,
            People = people,
            Active = register
                .Where(w => w.Status == WorkgroupStatus.Active)
                .OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            Pending = register
                .Where(w => w.Status is WorkgroupStatus.Applied or WorkgroupStatus.Referred)
                .OrderBy(w => w.AppliedAt)
                .ToList(),
            Archive = register
                .Where(w => w.Status is WorkgroupStatus.Dormant or WorkgroupStatus.Withdrawn
                    or WorkgroupStatus.Refused)
                .OrderByDescending(w => w.EndedAt ?? w.AppliedAt)
                .ToList()
        };
}

/// <summary>One group's page: the register entry, the people on it, and what this reader may do.</summary>
internal sealed class WorkgroupPageViewModel
{
    public required WorkgroupInfo Workgroup { get; init; }

    public required Instant Now { get; init; }

    public required Guid CurrentUserId { get; init; }

    /// <summary>Burner names and tiers for the roster and the log's attributions.</summary>
    public required IReadOnlyDictionary<Guid, UserInfo> People { get; init; }

    /// <summary>Board-team membership, for the roster's Board badge.</summary>
    public required IReadOnlySet<Guid> BoardUserIds { get; init; }

    public required bool IsMember { get; init; }

    public required bool IsCoordinator { get; init; }

    /// <summary>BoardOrAdmin: the decisions of §6 and the disposition.</summary>
    public required bool CanAdminister { get; init; }

    /// <summary>Member work is frozen unless the group is Active — see the authorization handler.</summary>
    public bool CanDoMemberWork => IsMember && Workgroup.AcceptsMemberWork();

    public string DisplayName(Guid? userId) =>
        userId is { } id && People.TryGetValue(id, out var info) ? info.BurnerName : "—";
}

/// <summary>The clause 1 application form, and the same fields when a member edits them.</summary>
internal sealed class WorkgroupFormViewModel
{
    public Guid? Id { get; init; }

    public string? Slug { get; init; }

    [Required]
    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string Purpose { get; set; } = string.Empty;

    /// <summary>The group's promise, in one line. Changing it writes a ScopeChanged entry.</summary>
    [Required]
    [StringLength(500)]
    public string Deliverable { get; set; } = string.Empty;

    public WorkgroupDeliverableKind DeliverableKind { get; set; }

    public WorkgroupAudience Audience { get; set; }

    public LocalDate? TargetDate { get; set; }

    [StringLength(500)]
    public string? DiscordChannelUrl { get; set; }

    /// <summary>Optional second name on the register; the applicant is always the first.</summary>
    public Guid? SecondCoordinatorUserId { get; set; }

    public static WorkgroupFormViewModel FromWorkgroup(WorkgroupInfo w) => new()
    {
        Id = w.Id,
        Slug = w.Slug,
        Name = w.Name,
        Purpose = w.Purpose,
        Deliverable = w.Deliverable,
        DeliverableKind = w.DeliverableKind,
        Audience = w.Audience,
        TargetDate = w.TargetDate,
        DiscordChannelUrl = w.DiscordChannelUrl
    };

    public WorkgroupApplication ToApplication() => new(
        Name, Purpose, Deliverable, DeliverableKind, Audience, TargetDate, DiscordChannelUrl,
        SecondCoordinatorUserId);

    public WorkgroupRegisterEdit ToEdit() => new(
        Name, Purpose, Deliverable, DeliverableKind, Audience, TargetDate, DiscordChannelUrl);
}

internal sealed class MeetingFormViewModel
{
    public Guid? Id { get; init; }

    public required string Slug { get; init; }

    [Required]
    [StringLength(200)]
    public string Title { get; set; } = string.Empty;

    public Instant StartUtc { get; set; }

    public Instant EndUtc { get; set; }

    [StringLength(200)]
    public string? Location { get; set; }

    [StringLength(500)]
    public string? LocationUrl { get; set; }

    /// <summary>A public meeting reaches everyone's community calendar through Calendar's fan-out.</summary>
    public bool IsPublic { get; set; }

    /// <summary>Markdown; "or summarised transcripts", per the guidance.</summary>
    public string? Minutes { get; set; }

    public static MeetingFormViewModel FromMeeting(string slug, WorkgroupMeetingInfo m) => new()
    {
        Id = m.Id,
        Slug = slug,
        Title = m.Title,
        StartUtc = m.StartUtc,
        EndUtc = m.EndUtc,
        Location = m.Location,
        LocationUrl = m.LocationUrl,
        IsPublic = m.IsPublic,
        Minutes = m.Minutes
    };

    public WorkgroupMeetingSave ToSave() =>
        new(Title, StartUtc, EndUtc, Location, LocationUrl, IsPublic, Minutes);
}

internal sealed class LogEntryFormViewModel
{
    public Guid? Id { get; init; }

    public required string Slug { get; init; }

    /// <summary>Update, Disclosure or Note — the kinds a human writes.</summary>
    public WorkgroupLogKind Kind { get; set; } = WorkgroupLogKind.Update;

    public LocalDate OccurredOn { get; set; }

    [StringLength(200)]
    public string? Title { get; set; }

    [Required]
    public string Body { get; set; } = string.Empty;

    public static LogEntryFormViewModel FromEntry(string slug, WorkgroupLogEntryInfo e) => new()
    {
        Id = e.Id,
        Slug = slug,
        Kind = e.Kind,
        OccurredOn = e.OccurredOn,
        Title = e.Title,
        Body = e.Body ?? string.Empty
    };

    public WorkgroupLogEntrySave ToSave() => new(Kind, OccurredOn, Title, Body);
}

internal sealed class DocumentFormViewModel
{
    public Guid? Id { get; init; }

    public required string Slug { get; init; }

    [Required]
    [StringLength(200)]
    public string Title { get; set; } = string.Empty;

    public WorkgroupDocumentKind Kind { get; set; }

    /// <summary>Markdown, sanitized on render. Groups draft in Drive and paste the publishable text here.</summary>
    public string Body { get; set; } = string.Empty;

    public static DocumentFormViewModel FromDocument(string slug, WorkgroupDocumentInfo d) => new()
    {
        Id = d.Id,
        Slug = slug,
        Title = d.Title,
        Kind = d.Kind,
        Body = d.Body
    };

    public WorkgroupDocumentSave ToSave() => new(Title, Kind, Body);
}

/// <summary>One document, read: the body, the comments grouped by category, and what the reader may do.</summary>
internal sealed class DocumentPageViewModel
{
    public required WorkgroupInfo Workgroup { get; init; }

    public required WorkgroupDocumentInfo Document { get; init; }

    public required Instant Now { get; init; }

    public required Guid CurrentUserId { get; init; }

    public required IReadOnlyDictionary<Guid, UserInfo> People { get; init; }

    public required bool IsMember { get; init; }

    public required bool CanAdminister { get; init; }

    public bool CanDoMemberWork => IsMember && Workgroup.AcceptsMemberWork();

    public bool IsOpenForComment => Document.IsOpenForComment(Now);

    /// <summary>Hidden comments read as hidden to everyone but the people who can moderate.</summary>
    public bool CanSeeHidden => IsMember || CanAdminister;

    public string DisplayName(Guid? userId) =>
        userId is { } id && People.TryGetValue(id, out var info) ? info.BurnerName : "—";
}

/// <summary>The admin queue of §15: everything waiting on the Secretary or the Board.</summary>
internal sealed class AdminQueueViewModel
{
    public required Instant Now { get; init; }

    public required IReadOnlyList<WorkgroupInfo> Register { get; init; }

    public required IReadOnlyDictionary<Guid, UserInfo> People { get; init; }

    public required string? RootDriveFolderId { get; init; }

    public IEnumerable<WorkgroupInfo> Pending => Register
        .Where(w => w.Status is WorkgroupStatus.Applied or WorkgroupStatus.Referred)
        .OrderBy(w => w.AppliedAt);

    public IEnumerable<WorkgroupInfo> Active => Register
        .Where(w => w.Status == WorkgroupStatus.Active)
        .OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase);

    public IEnumerable<(WorkgroupInfo Workgroup, WorkgroupDocumentInfo Document)> AwaitingDisposition =>
        Register
            .SelectMany(w => w.AwaitingDisposition().Select(d => (Workgroup: w, Document: d)))
            .OrderBy(x => x.Document.DeliveredAt);

    public IEnumerable<WorkgroupInfo> DormancyFlagged =>
        Active.Where(w => w.DormantSince is not null);

    public IEnumerable<WorkgroupInfo> AnnualReportDue =>
        Active.Where(w => w.IsAnnualReportDue(Now));

    public IEnumerable<WorkgroupInfo> StatusOverdue =>
        Active.Where(w => w.IsStatusOverdue(Now));

    public string DisplayName(Guid? userId) =>
        userId is { } id && People.TryGetValue(id, out var info) ? info.BurnerName : "—";
}

/// <summary>Bootstrapping (design §21): the same application fields, plus who and since when.</summary>
internal sealed class RegisterExistingViewModel
{
    public WorkgroupFormViewModel Application { get; set; } = new();

    [Required]
    public Guid CoordinatorUserId { get; set; }

    /// <summary>When the group really started; the register is backdated to it.</summary>
    public LocalDate RegisteredOn { get; set; }
}

/// <summary>The one Workgroups setting: the Drive folder every group's subfolder is created under.</summary>
internal sealed class WorkgroupsSettingsViewModel
{
    [Required]
    public string RootDriveFolderId { get; set; } = string.Empty;
}
