namespace Humans.Tickets.Services.Dtos;

internal enum AttendeeImportOutcome
{
    /// <summary>Exactly one verified UserEmail matches — set MatchedUserId, no creation.</summary>
    AttachVerified = 0,

    /// <summary>Multiple verified owners or multiple unverified rows match — skip for operator review.</summary>
    AmbiguousEmailMatches = 1,

    /// <summary>Only an unverified UserEmail row matches — delete it, then create new user with verified row.</summary>
    DeleteUnverifiedThenCreate = 2,

    /// <summary>No UserEmail row matches — create a brand-new user with verified row.</summary>
    CreateNewUser = 3,

    /// <summary>Attendee has no email — skip.</summary>
    SkipNoEmail = 4,

    /// <summary>Attendee is Void — skip (typically excluded from plan input).</summary>
    SkipVoided = 5,
}
