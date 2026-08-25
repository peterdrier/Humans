using Humans.MailerLite.Services.Dtos;

namespace Humans.MailerLite.Models;

internal sealed record MailerLiteImportPreviewViewModel(
    ImportPlan Plan,
    IReadOnlyList<SubscriberDecisionRow> Rows);

internal sealed record SubscriberDecisionRow(
    string Email,
    string MlStatus,
    Guid? MatchedUserId,
    SubscriberOutcome Outcome);
