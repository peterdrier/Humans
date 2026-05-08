namespace Humans.Application.DTOs.EmailProblems;

public enum EmailProblemKind
{
    MultipleIsPrimary = 1,
    MultipleIsGoogle = 2,
    ZeroIsPrimary = 3,
    ZeroIsGoogle = 4,
    SharedAcrossUsers = 5,
    Unverified = 6,
    OrphanUserEmail = 7,
    GhostExternalLogins = 8,
    LegacyIdentityEmailNotInUserEmails = 9
}
