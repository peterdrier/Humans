using Humans.Domain.Attributes;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

public class CampSeason
{
    public Guid Id { get; init; }

    public Guid CampId { get; init; }
    public Camp Camp { get; set; } = null!;

    public int Year { get; init; }
    public string Name { get; set; } = string.Empty;
    public LocalDate? NameLockDate { get; set; }
    public Instant? NameLockedAt { get; set; }

    public CampSeasonStatus Status { get; set; } = CampSeasonStatus.Pending;

    [MarkdownContent]
    public string BlurbLong { get; set; } = string.Empty;
    public string BlurbShort { get; set; } = string.Empty;
    public string Languages { get; set; } = string.Empty;

    public YesNoMaybe AcceptingMembers { get; set; }
    public YesNoMaybe KidsWelcome { get; set; }
    public KidsVisitingPolicy KidsVisiting { get; set; }
    [MarkdownContent]
    public string? KidsAreaDescription { get; set; }

    public PerformanceSpaceStatus HasPerformanceSpace { get; set; }
    public string? PerformanceTypes { get; set; }

    public List<CampVibe> Vibes { get; set; } = [];

    public AdultPlayspacePolicy AdultPlayspace { get; set; }

    // Placement
    public int MemberCount { get; set; }
    public SpaceSize? SpaceRequirement { get; set; }
    public SoundZone? SoundZone { get; set; }
    public ElectricalGrid? ElectricalGrid { get; set; }

    // Review
    public Guid? ReviewedByUserId { get; set; }
    public User? ReviewedByUser { get; set; }
    public string? ReviewNotes { get; set; }
    public Instant? ResolvedAt { get; set; }

    /// <summary>
    /// Number of Early Entry slots this season's camp may grant to its members.
    /// CampAdmin-managed. 0 = no EE this season. See docs/sections/Camps.md.
    /// </summary>
    public int EeSlotCount { get; set; }

    /// <summary>
    /// Reverse navigation: all <see cref="CampMember"/> rows for this season.
    /// Populated only when explicitly Included (e.g. in the admin read path).
    /// </summary>
    public ICollection<CampMember> Members { get; set; } = new List<CampMember>();

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }

    public CampSeason CreatePendingRenewal(Guid id, int year, Instant now) =>
        CreateRenewal(id, year, CampSeasonStatus.Pending, now);

    public CampSeason CreateApprovedRenewal(Guid id, int year, Instant now) =>
        CreateRenewal(id, year, CampSeasonStatus.Active, now);

    private CampSeason CreateRenewal(Guid id, int year, CampSeasonStatus status, Instant now) =>
        new()
        {
            Id = id,
            CampId = CampId,
            Year = year,
            Name = Name,
            Status = status,
            BlurbLong = BlurbLong,
            BlurbShort = BlurbShort,
            Languages = Languages,
            AcceptingMembers = AcceptingMembers,
            KidsWelcome = KidsWelcome,
            KidsVisiting = KidsVisiting,
            KidsAreaDescription = KidsAreaDescription,
            HasPerformanceSpace = HasPerformanceSpace,
            PerformanceTypes = PerformanceTypes,
            Vibes = [.. Vibes],
            AdultPlayspace = AdultPlayspace,
            MemberCount = MemberCount,
            SpaceRequirement = SpaceRequirement,
            SoundZone = SoundZone,
            ElectricalGrid = ElectricalGrid,
            CreatedAt = now,
            UpdatedAt = now
        };

    public void Approve(Guid reviewedByUserId, string? notes, Instant now)
    {
        EnsureStatus(CampSeasonStatus.Pending, "approve");

        Status = CampSeasonStatus.Active;
        ReviewedByUserId = reviewedByUserId;
        ReviewNotes = notes;
        ResolvedAt = now;
        UpdatedAt = now;
    }

    public void Reject(Guid reviewedByUserId, string notes, Instant now)
    {
        EnsureStatus(CampSeasonStatus.Pending, "reject");

        Status = CampSeasonStatus.Rejected;
        ReviewedByUserId = reviewedByUserId;
        ReviewNotes = notes;
        ResolvedAt = now;
        UpdatedAt = now;
    }

    public void Withdraw(Instant now)
    {
        if (Status != CampSeasonStatus.Pending && Status != CampSeasonStatus.Active)
        {
            throw InvalidTransition("withdraw");
        }

        Status = CampSeasonStatus.Withdrawn;
        UpdatedAt = now;
    }

    public CampSeasonStatus Reactivate(Instant now)
    {
        if (Status != CampSeasonStatus.Full && Status != CampSeasonStatus.Withdrawn)
        {
            throw InvalidTransition("reactivate");
        }

        Status = Status == CampSeasonStatus.Withdrawn
            ? CampSeasonStatus.Pending
            : CampSeasonStatus.Active;
        UpdatedAt = now;
        return Status;
    }

    private void EnsureStatus(CampSeasonStatus status, string verb)
    {
        if (Status != status)
        {
            throw InvalidTransition(verb);
        }
    }

    private InvalidOperationException InvalidTransition(string verb) =>
        new($"Cannot {verb} a season with status {Status}.");
}
