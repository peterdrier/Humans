using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Surveys.Contracts;
using Humans.Surveys.Data;
using Humans.Surveys.Domain;
using NodaTime;

namespace Humans.Surveys.Services;

internal sealed class SurveyAuthoring(
    ISurveyRepository repo,
    IAuditLogService auditLog,
    IClock clock,
    IFileStorage fileStorage,
    ILogger<SurveyService> logger) : IApplicationService
{
    private readonly SurveyImageUploads imageUploads = new(fileStorage, logger);

    public async Task<SurveySaveResult> CreateAsync(SurveyEditInput input, Guid actorUserId, CancellationToken ct = default)
    {
        var error = SurveyDefinitionValidation.AudienceConfigurationError(
            input.AudienceType, input.AudienceTeamId, input.AudienceLoggedInSince, requireAudience: false);
        if (error is not null) return new(Error: error);
        var invitationEmailSubject = NormalizeLocalizedText(input.InvitationEmailSubject);
        var invitationEmailMessage = NormalizeLocalizedText(input.InvitationEmailMessage);
        error = SurveyDefinitionValidation.ValidateInvitationEmailCopy(invitationEmailSubject, invitationEmailMessage);
        if (error is not null) return new(Error: error);
        var now = clock.GetCurrentInstant();
        var surveyId = Guid.NewGuid();
        var (publicSlug, slugError) = SurveyDefinitionValidation.PreparePublicSlugForWrite(input.PublicSlug);
        if (slugError is not null) return new(Error: slugError);
        var (questions, newStoragePaths, questionError) = await PrepareQuestionsAsync(
            surveyId, input, existing: null, allowRankedAvailabilityChanges: false, ct);
        if (questionError is not null) return new(Error: questionError);

        var survey = new Survey
        {
            Id = surveyId,
            Status = SurveyStatus.Draft,
            CreatedByUserId = actorUserId,
            CreatedAt = now,
        };
        ApplyEditableFields(survey, input, questions, invitationEmailSubject, invitationEmailMessage, publicSlug, now);

        try
        {
            await repo.AddAsync(survey, ct);
        }
        catch
        {
            await CleanupFailedInformationImageUploadAsync(surveyId, newStoragePaths);
            throw;
        }
        logger.LogInformation("Survey {SurveyId} created by {UserId}", surveyId, actorUserId);
        await auditLog.LogAsync(SurveysAuditActions.SurveyCreated, AuditEntityTypes.Survey, surveyId,
            $"Created survey '{survey.Title.Resolve(survey.DefaultCulture, survey.DefaultCulture)}'", actorUserId);
        return new(surveyId);
    }

    public Task<SurveySaveResult> UpdateAsync(Guid surveyId, SurveyEditInput input, SurveyViewer viewer, CancellationToken ct = default)
        => UpdateCoreAsync(surveyId, input, viewer, allowRankedAvailabilityChanges: false, ct);

    internal async Task<SurveySaveResult> UpdateCoreAsync(
        Guid surveyId,
        SurveyEditInput input,
        SurveyViewer viewer,
        bool allowRankedAvailabilityChanges,
        CancellationToken ct)
    {
        var actorUserId = viewer.UserId;
        var error = SurveyDefinitionValidation.AudienceConfigurationError(
            input.AudienceType, input.AudienceTeamId, input.AudienceLoggedInSince, requireAudience: false);
        if (error is not null) return new(Error: error);
        var invitationEmailSubject = NormalizeLocalizedText(input.InvitationEmailSubject);
        var invitationEmailMessage = NormalizeLocalizedText(input.InvitationEmailMessage);
        error = SurveyDefinitionValidation.ValidateInvitationEmailCopy(invitationEmailSubject, invitationEmailMessage);
        if (error is not null) return new(Error: error);
        var now = clock.GetCurrentInstant();
        var existing = await repo.GetByIdAsync(surveyId, ct);
        if (existing is null) return new(Error: new("Surveys_PreviewNotFound", "Survey not found."));
        // The controller's resource handler says the same thing before rendering a button; this is
        // the enforcing copy, so an edit refused there is refused here too whoever the caller is.
        if (!viewer.ManagesEverySurvey && existing.CreatedByUserId != actorUserId)
            return new(Error: new("Surveys_Validation_SaveNotAuthor", "Only the survey's author may edit it."));
        if (existing.IsAsociadoVote == true
            && existing.Status != SurveyStatus.Draft
            && !allowRankedAvailabilityChanges)
        {
            return new(Error: new("Surveys_Validation_VoteReadOnly",
                "An Asociado vote cannot be edited after it has opened."));
        }
        if (existing.Status != SurveyStatus.Draft
            && (existing.IsAsociadoVote == true) != input.IsAsociadoVote)
        {
            return new(Error: new("Surveys_Validation_VoteModeFrozen",
                "Asociado vote mode cannot change after the survey has opened."));
        }
        var (publicSlug, slugError) = SurveyDefinitionValidation.PreparePublicSlugForWrite(input.PublicSlug);
        if (slugError is not null) return new(Error: slugError);
        var (questions, newStoragePaths, questionError) = await PrepareQuestionsAsync(
            surveyId, input, existing, allowRankedAvailabilityChanges, ct);
        if (questionError is not null) return new(Error: questionError);

        var survey = new Survey { Id = surveyId };
        ApplyEditableFields(survey, input, questions, invitationEmailSubject, invitationEmailMessage, publicSlug, now);

        // Diffed against the no-tracking snapshot so the audit trail names what changed.
        var changeSummary = SurveyChangeDescription.DescribeSurveyChanges(existing, survey);
        try
        {
            await repo.UpdateAsync(survey, ct);
        }
        catch
        {
            await CleanupFailedInformationImageUploadAsync(surveyId, newStoragePaths);
            throw;
        }
        await auditLog.LogAsync(SurveysAuditActions.SurveyUpdated, AuditEntityTypes.Survey, surveyId, changeSummary, actorUserId);
        return new(surveyId);
    }

    private async Task<(List<SurveyQuestion> Questions, IReadOnlyList<string> NewStoragePaths, SurveyAuthoringError? Error)>
        PrepareQuestionsAsync(
            Guid surveyId, SurveyEditInput input, Survey? existing, bool allowRankedAvailabilityChanges, CancellationToken ct)
    {
        var prepared = await imageUploads.PrepareAsync(surveyId, input, existing, ct);
        if (prepared.Error is not null) return ([], [], prepared.Error);
        try
        {
            var questions = SurveyQuestionMapping.MapQuestions(surveyId, prepared.Input);
            if (existing is not null && !allowRankedAvailabilityChanges)
            {
                var existingById = existing.Questions.ToDictionary(question => question.Id);
                foreach (var question in questions.Where(question => question.Type == SurveyQuestionType.RankedChoice))
                {
                    if (existingById.TryGetValue(question.Id, out var persisted))
                    {
                        question.RankedUnavailableOptionValues =
                            persisted.RankedUnavailableOptionValues?.ToList();
                    }
                }
            }
            var error = SurveyDefinitionValidation.Validate(questions, input);
            if (error is null && existing is not null && await repo.HasSavedAnswersAsync(surveyId, ct))
                error = SurveyDefinitionValidation.ValidateRankedDefinitionFrozen(existing.Questions, questions);
            if (error is null) return (questions, prepared.NewStoragePaths, null);

            await imageUploads.DeleteBestEffortAsync(prepared.NewStoragePaths, CancellationToken.None);
            return ([], [], error);
        }
        catch
        {
            await imageUploads.DeleteBestEffortAsync(prepared.NewStoragePaths, CancellationToken.None);
            throw;
        }
    }

    private static void ApplyEditableFields(
        Survey survey, SurveyEditInput input, List<SurveyQuestion> questions,
        LocalizedText invitationEmailSubject, LocalizedText invitationEmailMessage, string? publicSlug, Instant now)
    {
        survey.Title = input.Title;
        survey.Intro = input.Intro;
        survey.ThankYou = input.ThankYou;
        survey.InvitationEmailSubject = invitationEmailSubject;
        survey.InvitationEmailMessage = invitationEmailMessage;
        survey.DefaultCulture = input.DefaultCulture;
        survey.AllowAnonymous = input.AllowAnonymous;
        survey.IsAsociadoVote = input.IsAsociadoVote;
        survey.OpensAt = input.OpensAt;
        survey.ClosesAt = input.ClosesAt;
        survey.AudienceType = input.AudienceType;
        survey.AudienceTeamId = input.AudienceTeamId;
        survey.AudienceLoggedInSince = input.AudienceLoggedInSince;
        survey.PublicSlug = publicSlug;
        survey.UpdatedAt = now;
        survey.Questions = questions;
    }

    private static LocalizedText NormalizeLocalizedText(LocalizedText text)
        => new(text.Values.ToDictionary(
            pair => pair.Key,
            pair => pair.Value?.Trim() ?? string.Empty,
            StringComparer.OrdinalIgnoreCase));

    private async Task CleanupFailedInformationImageUploadAsync(
        Guid surveyId, IReadOnlyList<string> newStoragePaths)
    {
        if (newStoragePaths.Count == 0) return;
        try
        {
            // A failed acknowledgement may follow a committed save. Retain any existing
            // survey's image versions, just as successful edits do for other open builders.
            if (await repo.GetByIdAsync(surveyId, CancellationToken.None) is null)
                await imageUploads.DeleteBestEffortAsync(newStoragePaths, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to verify survey {SurveyId} after a failed save; retaining newly uploaded images", surveyId);
        }
    }
}
