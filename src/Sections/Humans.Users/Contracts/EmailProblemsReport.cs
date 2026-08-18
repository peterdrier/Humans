using NodaTime;

namespace Humans.Users.Contracts;

public sealed record EmailProblemsReport(
    Instant ScannedAt,
    IReadOnlyList<EmailProblem> Problems);
