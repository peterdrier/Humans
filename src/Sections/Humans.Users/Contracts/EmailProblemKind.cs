namespace Humans.Users.Contracts;

public enum EmailProblemKind
{
    MultipleIsPrimary = 1,
    ZeroIsPrimary = 3,
    Unverified = 6,
    OrphanUserEmail = 7,
    GhostExternalLogins = 8,
    LegacyIdentityEmailNotInUserEmails = 9
}
