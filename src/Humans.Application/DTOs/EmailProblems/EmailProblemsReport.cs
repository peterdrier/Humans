using NodaTime;

namespace Humans.Application.DTOs.EmailProblems;

public sealed record EmailProblemsReport(
    Instant ScannedAt,
    IReadOnlyList<EmailProblem> Problems);
