using Humans.Mailer.Services.Dtos;
using NodaTime;

namespace Humans.Mailer.Models;

internal sealed record MailerImportPreviewViewModel(
    ImportPlan Plan,
    IReadOnlyList<SubscriberDecisionRow> Rows);

internal sealed record SubscriberDecisionRow(
    string Email,
    string MlStatus,
    Instant? MlLastActionAt,
    Guid? MatchedUserId,
    SubscriberOutcome Outcome);
