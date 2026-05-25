namespace Humans.Application.Interfaces.Tickets.Dtos;

/// <summary>
/// Per-attendee classification produced by
/// <see cref="Humans.Application.Interfaces.Tickets.IAttendeeContactImportService.BuildPlanAsync"/>.
/// Mirrors the Mailer import's outcome shape — verified matches attach,
/// unverified matches are deleted-then-created (squatter protection),
/// no match creates a new user with a verified UserEmail row.
/// </summary>
public enum AttendeeImportOutcome
{
    /// <summary>Exactly one verified UserEmail matches — set MatchedUserId, no creation.</summary>
    AttachVerified = 0,

    /// <summary>&gt;1 verified users own this email — skip with LogError (data-integrity).</summary>
    AmbiguousMultipleVerified = 1,

    /// <summary>Only an unverified UserEmail row matches — delete it, then create new user with verified row.</summary>
    DeleteUnverifiedThenCreate = 2,

    /// <summary>No UserEmail row matches — create a brand-new user with verified row.</summary>
    CreateNewUser = 3,

    /// <summary>Attendee has no email — skip.</summary>
    SkipNoEmail = 4,

    /// <summary>Attendee is Void — skip (typically excluded from plan input).</summary>
    SkipVoided = 5,
}
