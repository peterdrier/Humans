using Humans.AuditLog.Contracts;
using Humans.Base.Enums;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Surveys.Contracts;
using Humans.Surveys.Data;
using Humans.Surveys.Domain;
using NodaTime;

namespace Humans.Surveys.Services;

internal sealed class SurveyReview(
    ISurveyRepository repo,
    IAuditLogService auditLog,
    IClock clock,
    SurveyAudience audience,
    SurveyInvitationDelivery invitationDelivery) : IApplicationService
{
    public async Task<SurveyReviewResult> OpenAsync(Guid surveyId, Guid actorUserId, CancellationToken ct = default)
    {
        var status = await repo.GetStatusAsync(surveyId, ct);
        if (status is null)
            return new(Refusal: "Survey not found.");
        if (status == SurveyStatus.Open) return new();
        // A submission pending approval can only become Open via ApproveAndSendAsync — that is
        // the one step that also sends invitations and records the approval (Workgroups §11).
        if (status == SurveyStatus.PendingApproval)
            return new(Refusal: "A survey pending approval must be approved or rejected, not opened directly.");
        if (status == SurveyStatus.Closed)
        {
            var survey = await repo.GetByIdAsync(surveyId, ct);
            if (survey is null)
                return new(Refusal: "Survey not found.");
            if (survey.IsAsociadoVote == true)
                return new(Refusal: "A closed Asociado vote cannot be reopened.");
        }

        await repo.SetStatusAsync(surveyId, SurveyStatus.Open, clock.GetCurrentInstant(), ct);
        await auditLog.LogAsync(SurveysAuditActions.SurveyOpened, AuditEntityTypes.Survey, surveyId, "Opened survey", actorUserId);
        return new();
    }

    public async Task<SurveyReviewResult> CloseAsync(Guid surveyId, Guid actorUserId, CancellationToken ct = default)
    {
        var status = await repo.GetStatusAsync(surveyId, ct);
        if (status is null)
            return new(Refusal: "Survey not found.");
        if (status == SurveyStatus.Closed) return new();
        // Closing a pending submission would walk it out of the approval gate: Closed reopens
        // through OpenAsync, so PendingApproval → Closed → Open would reach Open with no approval
        // recorded and no invitations sent. Approve or reject it instead.
        if (status == SurveyStatus.PendingApproval)
            return new(Refusal: "A survey pending approval must be approved or rejected, not closed.");

        await repo.SetStatusAsync(surveyId, SurveyStatus.Closed, clock.GetCurrentInstant(), ct);
        await auditLog.LogAsync(SurveysAuditActions.SurveyClosed, AuditEntityTypes.Survey, surveyId, "Closed survey", actorUserId);
        return new();
    }

    public async Task<SurveySubmitResult> SubmitForApprovalAsync(Guid surveyId, Guid actorUserId, CancellationToken ct = default)
    {
        var survey = await repo.GetByIdAsync(surveyId, ct);
        if (survey is null)
            return new SurveySubmitResult(false, "Surveys_PreviewNotFound");
        if (survey.CreatedByUserId != actorUserId)
            return new SurveySubmitResult(false, "Surveys_SubmitNotAuthor");
        if (survey.Status != SurveyStatus.Draft)
            return new SurveySubmitResult(false, "Surveys_SubmitNotDraft");

        var now = clock.GetCurrentInstant();
        await repo.SubmitForApprovalAsync(surveyId, now, ct);
        await auditLog.LogAsync(SurveysAuditActions.SurveySubmittedForApproval, AuditEntityTypes.Survey, surveyId,
            $"Submitted survey '{survey.Title.Resolve(survey.DefaultCulture, survey.DefaultCulture)}' for approval", actorUserId);
        return new SurveySubmitResult(true);
    }

    public async Task<SurveyReviewResult> ApproveAndSendAsync(Guid surveyId, SurveyViewer viewer, CancellationToken ct = default)
    {
        if (!viewer.ManagesEverySurvey)
            return new(Refusal: "Only Surveys Admin or Admin may approve a survey.");
        var survey = await repo.GetByIdAsync(surveyId, ct);
        if (survey is null)
            return new(Refusal: "Survey not found.");
        if (survey.Status != SurveyStatus.PendingApproval)
            return new(Refusal: "Only a survey pending approval can be approved.");

        // Send validates the audience, and Open is not a state the author can edit out of — so a
        // misconfigured audience has to fail here, before approval is persisted, leaving the
        // survey in the queue where a Surveys Admin can reject it back to the author.
        var audienceError = SurveyDefinitionValidation.AudienceConfigurationError(
            survey.AudienceType, survey.AudienceTeamId, survey.AudienceLoggedInSince, requireAudience: true);
        if (audienceError is not null)
            return new(Refusal: audienceError.DiagnosticMessage);

        // Well-formed is not the same as non-empty: a Team audience whose team has since been
        // deleted passes configuration and resolves to nobody. Resolve here too, so that case
        // also fails in the queue rather than opening a survey that invites no one.
        var recipients = await audience.ResolveRecipientIdsAsync(
            survey.AudienceType!.Value, survey.AudienceTeamId, survey.AudienceLoggedInSince, ct);
        if (recipients.Count == 0)
            return new(Refusal: "This survey's audience resolves to nobody to invite.");

        var now = clock.GetCurrentInstant();
        await repo.ApproveAsync(surveyId, now, ct);
        await auditLog.LogAsync(SurveysAuditActions.SurveyApproved, AuditEntityTypes.Survey, surveyId,
            $"Approved survey '{survey.Title.Resolve(survey.DefaultCulture, survey.DefaultCulture)}'; sending invitations",
            viewer.UserId, relatedEntityId: survey.CreatedByUserId, relatedEntityType: AuditSubjectTypes.User);

        // CancellationToken.None, not ct: approval is persisted, and a request-scoped token firing mid-send would
        // leave the survey Open with a partial invitation set.
        var sent = await invitationDelivery.SendInvitesAsync(surveyId, viewer.UserId, CancellationToken.None);
        return new(Send: sent, Refusal: sent.Refusal);
    }

    public async Task<SurveyReviewResult> RejectAsync(Guid surveyId, SurveyViewer viewer, string note, CancellationToken ct = default)
    {
        if (!viewer.ManagesEverySurvey)
            return new(Refusal: "Only Surveys Admin or Admin may reject a survey.");
        if (string.IsNullOrWhiteSpace(note))
            return new(Refusal: "A rejection note is required.");
        var survey = await repo.GetByIdAsync(surveyId, ct);
        if (survey is null)
            return new(Refusal: "Survey not found.");
        if (survey.Status != SurveyStatus.PendingApproval)
            return new(Refusal: "Only a survey pending approval can be rejected.");

        var now = clock.GetCurrentInstant();
        var trimmedNote = note.Trim();
        if (trimmedNote.Length > 4000)
            return new(Refusal: "A rejection note must be 4000 characters or fewer.");
        await repo.RejectAsync(surveyId, trimmedNote, now, ct);
        await auditLog.LogAsync(SurveysAuditActions.SurveyRejected, AuditEntityTypes.Survey, surveyId,
            $"Rejected survey '{survey.Title.Resolve(survey.DefaultCulture, survey.DefaultCulture)}': {trimmedNote}",
            viewer.UserId, relatedEntityId: survey.CreatedByUserId, relatedEntityType: AuditSubjectTypes.User);
        return new();
    }
}
