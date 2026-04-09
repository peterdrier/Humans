namespace Humans.Application.DTOs;

public record AdminDashboardData(
    int TotalMembers,
    int IncompleteSignup,
    int PendingApproval,
    int ActiveMembers,
    int MissingConsents,
    int Suspended,
    int PendingDeletion,
    int PendingApplications,
    int TotalApplications,
    int ApprovedApplications,
    int RejectedApplications,
    int ColaboradorApplied,
    int AsociadoApplied,
    IReadOnlyList<LanguageCount> LanguageDistribution);

public record LanguageCount(string Language, int Count);
