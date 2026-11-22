using NodaTime;

namespace Humans.Users.Services;

internal sealed record EmailProblemsReport(
    Instant ScannedAt,
    IReadOnlyList<EmailProblem> Problems);
