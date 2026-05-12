using Humans.Application.Interfaces.Mailer.Dtos;
using NodaTime;

namespace Humans.Web.Models.Mailer;

public sealed record MailerImportPreviewViewModel(
    ImportPlan Plan,
    IReadOnlyList<SubscriberDecisionRow> Rows);

public sealed record SubscriberDecisionRow(
    string EmailRedacted,
    string EmailFull,           // shown on row-expand
    string MlStatus,
    Instant? MlLastActionAt,
    Guid? MatchedUserId,
    SubscriberOutcome Outcome);
