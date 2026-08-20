using Humans.MailerLite.Services.Dtos;
using NodaTime;

namespace Humans.MailerLite.Models;

internal sealed record MailerLiteImportPreviewViewModel(
    ImportPlan Plan,
    IReadOnlyList<SubscriberDecisionRow> Rows);

internal sealed record SubscriberDecisionRow(
    string Email,
    string MlStatus,
    Instant? MlLastActionAt,
    Guid? MatchedUserId,
    SubscriberOutcome Outcome);
