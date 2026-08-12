using Humans.Mailer.Services.Dtos;
using NodaTime;

namespace Humans.Mailer.Models;

internal sealed record MailerDashboardViewModel(
    MailerLiteAccountSummary? MlSummary,
    IReadOnlyList<MailerLiteGroup>? Groups,
    int HumansMailerLiteContacts,
    int HumansMarketingOptedIn,
    int HumansMarketingOptedOut,
    Instant? LastReconciliationAt,
    string? LastReconciliationSummary,
    DriftReport? Drift,
    string? MlError,
    Instant? CacheFetchedAt,
    IReadOnlyList<AudienceCardRow> Audiences);

internal sealed record DriftReport(
    int HumansOptedOutMlActive,           // legal-trouble row
    int? HumansOptedInMlAbsent);          // service-quality row (null = not yet computed)
