using Humans.Users.Services;

namespace Humans.Users.Models;

/// <summary>The page behind an emailed confirmation link, in whichever state the link is in.</summary>
internal sealed class EmailConfirmViewModel
{
    public EmailConfirmationState State { get; init; }
    public string Token { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;

    /// <summary>The account that added the address; named when the browser is signed in as another one.</summary>
    public Guid AskingUserId { get; init; }
    public Guid SignedInUserId { get; init; }

    /// <summary>The signed-in account, when the address needs a merge.</summary>
    public MergeAccountCardViewModel? Mine { get; init; }

    /// <summary>The account that already has the address confirmed.</summary>
    public MergeAccountCardViewModel? Other { get; init; }

    /// <summary>Both accounts are filled in, so the person picks which profile is kept.</summary>
    public bool MustChoose { get; init; }
    public bool ChoiceMissing { get; init; }
}

/// <summary>
/// One of a person's own two accounts, with enough on it to tell which one they have been using. Shown only
/// to someone who proved control of both, so it carries the legal name and every sign-in address.
/// </summary>
internal sealed class MergeAccountCardViewModel
{
    public required ProfileSummaryViewModel Summary { get; init; }
    public bool IsSignedIn { get; init; }

    /// <summary>Both accounts are filled in: the card carries the "keep this profile" choice.</summary>
    public bool MustChoose { get; set; }
    public IReadOnlyList<string> Roles { get; init; } = [];
    public int ShiftsAhead { get; init; }
    public int ShiftsWorked { get; init; }
    public int Tickets { get; init; }
    public IReadOnlyList<string> SignIns { get; init; } = [];
    public DateTime Created { get; init; }
    public DateTime? LastSignIn { get; init; }
}
