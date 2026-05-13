using Humans.Application.Interfaces.Mailer.Dtos;
using NodaTime;

namespace Humans.Web.Models.Mailer;

public sealed record MailerDashboardViewModel(
    MailerLiteAccountSummary? MlSummary,
    IReadOnlyList<MailerLiteGroup>? Groups,
    int HumansMailerLiteContacts,
    int HumansMarketingOptedIn,
    int HumansMarketingOptedOut,
    Instant? LastReconciliationAt,
    string? LastReconciliationSummary,
    DriftReport? Drift,
    string? MlError,
    Instant? CacheFetchedAt);

public sealed record DriftReport(
    int HumansOptedOutMlActive,           // legal-trouble row
    int? HumansOptedInMlAbsent);          // service-quality row (null = not yet computed)
