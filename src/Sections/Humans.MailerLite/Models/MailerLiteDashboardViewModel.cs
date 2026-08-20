using Humans.MailerLite.Services.Dtos;
using NodaTime;

namespace Humans.MailerLite.Models;

internal sealed record MailerLiteDashboardViewModel(
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
