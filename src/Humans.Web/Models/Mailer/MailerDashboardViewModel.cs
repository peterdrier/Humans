using Humans.Application.Interfaces.Mailer.Dtos;
using NodaTime;

namespace Humans.Web.Models.Mailer;

public sealed record MailerDashboardViewModel(
    MailerLiteAccountSummary MlSummary,
    IReadOnlyList<MailerLiteGroup> Groups,
    int HumansMailerLiteContacts,
    int HumansMarketingOptedIn,
    int HumansMarketingOptedOut,
    int ForgottenSkipListSize,
    Instant? LastReconciliationAt,
    string? LastReconciliationSummary,
    DriftReport Drift);

public sealed record DriftReport(
    int HumansOptedOutMlActive,           // legal-trouble row
    int? HumansOptedInMlAbsent,           // service-quality row (null = not yet computed)
    int ForgottenButMlActive);            // GDPR row
