using Humans.Google.Contracts;
using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using Humans.Surveys.Data;
using Humans.Surveys.Contracts;
using Humans.Base.Interfaces;
using NodaTime;
using Humans.Surveys.Domain;

namespace Humans.Surveys.Services;

internal sealed class SurveyService(
    ISurveyRepository repo,
    IAuditLogService auditLog,
    IClock clock,
    ILogger<SurveyService> logger,
    IUserService userService,
    SurveyAudience audience,
    ISurveyInvitations invitationDelivery,
    ISurveyInviteTokenProvider tokenProvider,
    IGoogleTranslationService translation,
    IFileStorage fileStorage) : ISurveyService, IUserDataContributor, IUserMerge
{
    public Task<bool> HasSavedAnswersAsync(Guid surveyId, CancellationToken ct = default)
        => repo.HasSavedAnswersAsync(surveyId, ct);

    public async Task<int> PreFillTranslationsAsync(
        Guid surveyId, IReadOnlyList<string> targetCultures, SurveyViewer viewer, CancellationToken ct = default)
    {
        var detail = await GetForEditAsync(surveyId, ct)
            ?? throw new InvalidOperationException("Survey not found.");
        if (detail.Editable.IsAsociadoVote && detail.Status != SurveyStatus.Draft)
        {
            throw new InvalidOperationException(
                "An Asociado vote cannot be edited after it has opened.");
        }
        var (input, filled) = await FillAsync(detail.Editable, targetCultures, ct);
        if (filled == 0) return 0;

        (await UpdateAsync(surveyId, input, viewer, ct)).RequireSuccess();
        logger.LogInformation(
            "Survey {SurveyId}: pre-filled {Count} missing translations from {Source}",
            surveyId, filled, detail.Editable.DefaultCulture);
        return filled;
    }

    /// <inheritdoc />

    public async Task<SurveySaveResult> SetRankedAvailabilityAsync(
        Guid surveyId,
        Guid questionId,
        IReadOnlyList<string> unavailableValues,
        SurveyViewer viewer,
        CancellationToken ct = default)
    {
        var detail = await GetForEditAsync(surveyId, ct);
        if (detail is null)
            return new(Error: new(null, "Survey not found."));
        if (detail.Status != SurveyStatus.Closed)
            return new(Error: new(null, "Candidate availability can only change after the vote closes."));
        var question = detail.Editable.Questions.FirstOrDefault(candidate => candidate.Id == questionId);
        if (question?.Type != SurveyQuestionType.RankedChoice)
            return new(Error: new(null, "Ranked-choice question not found."));
        var known = question.Options.Select(option => option.Value).ToHashSet(StringComparer.Ordinal);
        var normalized = unavailableValues
            .Where(known.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var updatedQuestions = detail.Editable.Questions
            .Select(candidate => candidate.Id == questionId
                ? candidate with { RankedUnavailableOptionValues = normalized }
                : candidate)
            .ToList();
        return await UpdateCoreAsync(
            surveyId,
            detail.Editable with { Questions = updatedQuestions },
            viewer,
            allowRankedAvailabilityChanges: true,
            ct);
    }

    /// <summary>
    /// Only Identified responses carry a <c>UserId</c>; for CompletionTracked/Anonymous respondents and the merely
    /// invited, the invitation slice is the record instead. Every slice is always emitted (empty, never null) so the
    /// export keys stay stable.
    /// </summary>

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [SurveyResponses] =
                "Partially retained: the invitation is deleted and the response is severed from " +
                "the person (UserId and InvitationId dropped, Anonymity forced to Anonymous), but " +
                "the answers themselves survive as an anonymous data point in the survey's " +
                "results — GDPR Art. 17(3)(b). They are no longer attributable to anyone.",
            [AuthoredSurveys] =
                "Partially retained: the authorship link is dropped and any rejection note " +
                "deleted, but the survey and its questions survive as the association's own " +
                "record of what it asked — GDPR Art. 17(3)(b).",
            [SurveyInvitations] =
                "Erased: AnonymizeResponsesForUserAsync deletes the person's invitation rows " +
                "outright, so nothing of this section's record that they were asked survives."
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>
    /// The identity link is dropped and the response demoted to Anonymous — the
    /// answers survive as anonymous research data that is no longer personal data.
    /// </summary>
    public async Task EraseForUserAsync(Guid userId, CancellationToken ct)
    {
        await repo.AnonymizeResponsesForUserAsync(userId, ct);
        await repo.ClearAuthorshipForUserAsync(userId, ct);
    }

    /// <summary>
    /// Account merge: authorship follows the survivor. Responses and invitations retain
    /// their original account IDs; GDPR export follows the identity chain, and the deletion
    /// pipeline erases each account in that chain.
    /// </summary>
    public Task ReassignAsync(
        Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct)
        => repo.ReassignAuthorshipAsync(mergedFromUserId, mergedToUserId, now, ct);

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

    public async Task<IReadOnlyList<SurveyAdminSummary>> GetAdminSummariesAsync(SurveyViewer viewer, CancellationToken ct = default)
    {
        var surveys = await repo.GetAllSummariesAsync(ct);
        var scoped = viewer.SeesEverySurvey
            ? surveys
            : surveys.Where(s => s.CreatedByUserId == viewer.UserId).ToList();
        if (scoped.Count == 0) return [];

        var invited = await repo.GetInvitedCountsBySurveyAsync(ct);
        var responses = await repo.GetResponseCountsBySurveyAsync(ct);

        return scoped.Select(s => new SurveyAdminSummary(
            s.Id,
            s.Title.Resolve(s.DefaultCulture, s.DefaultCulture),
            s.Status,
            invited.GetValueOrDefault(s.Id),
            responses.GetValueOrDefault(s.Id),
            s.CreatedByUserId,
            s.SubmittedAt,
            s.RejectionNote)).ToList();
    }

    public async Task<IReadOnlyList<SurveyPendingApprovalItem>> GetPendingApprovalQueueAsync(CancellationToken ct = default)
    {
        return (await repo.GetAllSummariesAsync(ct))
            .Where(s => s.Status == SurveyStatus.PendingApproval)
            .Select(s => new SurveyPendingApprovalItem(
                s.Id,
                s.Title.Resolve(s.DefaultCulture, s.DefaultCulture),
                s.CreatedByUserId,
                s.SubmittedAt))
            .ToList();
    }

    public async Task<SurveyOfficialLink?> GetOfficialLinkAsync(
        Guid surveyId,
        Guid userId,
        CancellationToken ct = default)
    {
        var survey = await repo.GetByIdAsync(surveyId, ct);
        if (survey?.Status != SurveyStatus.Open) return null;

        var invitation = (await repo.GetInvitationsAsync(surveyId, ct))
            .FirstOrDefault(candidate =>
                candidate.UserId == userId
                && candidate.SentAt is not null
                && !candidate.Completed);
        if (invitation is not null)
        {
            return new SurveyOfficialLink(tokenProvider.Create(invitation.Id), null);
        }

        return string.IsNullOrWhiteSpace(survey.PublicSlug)
            ? null
            : new SurveyOfficialLink(null, survey.PublicSlug);
    }
    public async Task<SurveyDetail?> GetForEditAsync(Guid surveyId, CancellationToken ct = default)
    {
        var s = await repo.GetByIdAsync(surveyId, ct);
        if (s is null) return null;

        var input = new SurveyEditInput(
            s.Title, s.Intro, s.ThankYou, s.InvitationEmailSubject, s.InvitationEmailMessage,
            s.DefaultCulture, s.AllowAnonymous, s.OpensAt, s.ClosesAt,
            s.AudienceType, s.AudienceTeamId, s.AudienceLoggedInSince, s.PublicSlug,
            SurveyQuestionMapping.ToQuestionInputs(s),
            s.IsAsociadoVote == true);

        return new SurveyDetail(s.Id, s.Status, input, s.CreatedByUserId, s.RejectionNote);
    }

    internal const string SurveyResponses = "SurveyResponses";
    internal const string AuthoredSurveys = "AuthoredSurveys";
    internal const string SurveyInvitations = "SurveyInvitations";

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await userService.GetUserInfoAsync(userId, ct);
        var userIds = user?.AllUserIds ?? [userId];
        var responses = new List<SurveyResponse>();
        var invitations = new List<SurveyInvitation>();
        foreach (var id in userIds)
        {
            responses.AddRange(await repo.GetIdentifiedResponsesForUserAsync(id, ct));
            invitations.AddRange(await repo.GetInvitationsForUserAsync(id, ct));
        }

        // Load each distinct survey definition once (few at this scale) to resolve titles, prompts, and labels.
        var definitions = new Dictionary<Guid, Survey>();
        foreach (var surveyId in responses.Select(r => r.SurveyId).Distinct())
        {
            var survey = await repo.GetByIdAsync(surveyId, ct);
            if (survey is not null) definitions[surveyId] = survey;
        }

        var shaped = responses
            .OrderBy(r => r.SubmittedAt)
            .Select(r =>
            {
                definitions.TryGetValue(r.SurveyId, out var survey);
                var culture = string.IsNullOrEmpty(r.Culture)
                    ? survey?.DefaultCulture ?? "en"
                    : r.Culture;

                var answerContext = new SurveyAnswerProjection(survey, culture);

                return new
                {
                    Survey = survey?.Title.Resolve(culture, survey.DefaultCulture) ?? r.SurveyId.ToString(),
                    SubmittedAt = r.SubmittedAt.ToIso8601(),
                    Culture = culture,
                    Answers = r.Answers.Select(a => new
                    {
                        Question = answerContext.PromptFor(a),
                        SelectedLabels = answerContext.ResolveSelectedLabels(a),
                        GridSelections = SurveyAnswerProjection.CopyGridSelections(a),
                        GridSelectionLabels = answerContext.ResolveGridSelections(a),
                        RankedBallot = SurveyAnswerProjection.CopyRankedBallot(a),
                        RankedBallotLabels = answerContext.ResolveRankedBallot(a),
                        a.TextValue,
                        a.RatingValue,
                    }).ToList(),
                };
            })
            .ToList();

        // Authoring is open to any approved human, so a member's own surveys are their
        // personal data too — Drafts nobody else can see included.
        var authored = (await repo.GetSurveysAuthoredByAsync(user?.Id ?? userId, ct))
            .OrderBy(s => s.CreatedAt)
            .Select(s => new
            {
                Survey = s.Title.Resolve(s.DefaultCulture, s.DefaultCulture),
                Status = s.Status.ToString(),
                s.RejectionNote,
                CreatedAt = s.CreatedAt.ToIso8601(),
                QuestionCount = s.Questions.Count
            })
            .ToList();

        // The invitation ledger. Most responses are not Identified, so for someone who was only
        // invited — or who answered under completion tracking — this is the only record of theirs
        // the section holds. The timestamps here are invitation-side and say nothing about when a
        // response arrived, so they do not correlate with an anonymous answer.
        foreach (var surveyId in invitations.Select(i => i.SurveyId).Distinct())
        {
            if (definitions.ContainsKey(surveyId)) continue;
            var survey = await repo.GetByIdAsync(surveyId, ct);
            if (survey is not null) definitions[surveyId] = survey;
        }

        var invited = invitations
            .OrderBy(i => i.CreatedAt)
            .Select(i =>
            {
                definitions.TryGetValue(i.SurveyId, out var survey);
                var culture = survey?.DefaultCulture ?? "en";
                return new
                {
                    Survey = survey?.Title.Resolve(culture, culture) ?? i.SurveyId.ToString(),
                    InvitedAt = i.CreatedAt.ToIso8601(),
                    SentAt = i.SentAt.ToIso8601(),
                    EmailStatus = i.LatestEmailStatus?.ToString(),
                    ReminderSentAt = i.ReminderSentAt.ToIso8601(),
                    i.Started,
                    i.Completed
                };
            })
            .ToList();

        return
        [
            new UserDataSlice(SurveyResponses, shaped),
            new UserDataSlice(AuthoredSurveys, authored),
            new UserDataSlice(SurveyInvitations, invited)
        ];
    }

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
        var prepared = await PrepareAsync(surveyId, input, existing, ct);
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

            await DeleteBestEffortAsync(prepared.NewStoragePaths, CancellationToken.None);
            return ([], [], error);
        }
        catch
        {
            await DeleteBestEffortAsync(prepared.NewStoragePaths, CancellationToken.None);
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
                await DeleteBestEffortAsync(newStoragePaths, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to verify survey {SurveyId} after a failed save; retaining newly uploaded images", surveyId);
        }
    }

    internal const int MaxInformationImages = 5;
    private const int MaxInformationImageFileNameLength = 256;
    private const long MaxInformationImageBytes = 10 * 1024 * 1024;
    private static readonly HashSet<string> AllowedInformationImageContentTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/webp" };
    private static readonly HashSet<string> AllowedInformationImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };

    internal async Task<PreparedInformationImages> PrepareAsync(
        Guid surveyId,
        SurveyEditInput input,
        Survey? existing,
        CancellationToken ct)
    {
        var existingImages = existing?.Questions
            .SelectMany(question => question.InformationImages ?? [])
            .ToDictionary(image => image.Id)
            ?? new Dictionary<Guid, SurveyInformationImage>();
        var newStoragePaths = new List<string>();
        var preparedQuestions = new List<QuestionInput>(input.Questions.Count);

        try
        {
            foreach (var question in input.Questions)
            {
                var questionId = question.Id ?? Guid.NewGuid();
                if (question.Type != SurveyQuestionType.Information)
                {
                    preparedQuestions.Add(question with
                    {
                        Id = questionId,
                        InformationImages = null,
                    });
                    continue;
                }

                var requestedImages = question.InformationImages ?? [];
                if (requestedImages.Count > MaxInformationImages)
                {
                    return await RejectAsync(new("Surveys_Validation_ImagesLimit",
                        $"An Information item can have at most {MaxInformationImages} images.", MaxInformationImages));
                }

                var preparedImages = new List<InformationImageInput>(requestedImages.Count);
                foreach (var requested in requestedImages)
                {
                    if (requested.Upload is { } upload)
                    {
                        var imageError = ValidateInformationImage(upload);
                        if (imageError is not null) return await RejectAsync(imageError);
                        var fileName = DisplayFileName(upload.FileName);
                        // Replacements get a fresh key so a failed database save cannot delete or
                        // overwrite the still-authoritative existing file.
                        var imageId = Guid.NewGuid();
                        var extension = Path.GetExtension(fileName);
                        var storagePath = $"uploads/surveys/{surveyId}/{questionId}/{imageId}{extension}";
                        await fileStorage.SaveAsync(storagePath, upload.Content, ct);
                        newStoragePaths.Add(storagePath);
                        preparedImages.Add(requested with
                        {
                            Id = imageId,
                            StoragePath = storagePath,
                            ContentType = upload.ContentType,
                            FileName = fileName,
                            Upload = null,
                        });
                        continue;
                    }

                    if (requested.Id is { } existingId
                        && existingImages.TryGetValue(existingId, out var persisted))
                    {
                        preparedImages.Add(requested with
                        {
                            StoragePath = persisted.StoragePath,
                            ContentType = persisted.ContentType,
                            FileName = persisted.FileName,
                            Upload = null,
                        });
                        continue;
                    }

                    return await RejectAsync(new("Surveys_Validation_ImageRequired",
                        "Select an image file for every image row. " +
                        "If a previous save failed, select the file again."));
                }

                preparedQuestions.Add(question with
                {
                    Id = questionId,
                    IsRequired = false,
                    RatingMin = null,
                    RatingMax = null,
                    RatingMinLabel = LocalizedText.Empty,
                    RatingMaxLabel = LocalizedText.Empty,
                    Options = [],
                    GridSelectionMode = null,
                    GridRows = null,
                    InformationImages = preparedImages,
                });
            }
        }
        catch
        {
            await DeleteBestEffortAsync(newStoragePaths, CancellationToken.None);
            throw;
        }

        return new PreparedInformationImages(
            input with { Questions = preparedQuestions },
            newStoragePaths);

        async Task<PreparedInformationImages> RejectAsync(SurveyAuthoringError error)
        {
            await DeleteBestEffortAsync(newStoragePaths, CancellationToken.None);
            return new(input, [], error);
        }
    }

    private static SurveyAuthoringError? ValidateInformationImage(SurveyImageUpload upload)
    {
        var fileName = DisplayFileName(upload.FileName);
        if (upload.Length <= 0)
        {
            return new("Surveys_Validation_ImageEmpty", "The selected image is empty.");
        }
        if (!AllowedInformationImageContentTypes.Contains(upload.ContentType))
        {
            return new("Surveys_Validation_ImageContentType", "Only JPEG, PNG, and WebP images are allowed.");
        }
        if (upload.Length > MaxInformationImageBytes)
        {
            return new("Surveys_Validation_ImageSize", "Each Information image must be under 10 MB.");
        }
        if (!AllowedInformationImageExtensions.Contains(Path.GetExtension(fileName)))
        {
            return new("Surveys_Validation_ImageExtension", "Image filenames must end in .jpg, .jpeg, .png, or .webp.");
        }
        if (fileName.Length > MaxInformationImageFileNameLength)
        {
            return new("Surveys_Validation_ImageFileNameLength",
                $"Image filename must be {MaxInformationImageFileNameLength} characters or fewer.", MaxInformationImageFileNameLength);
        }

        return null;
    }

    private static string DisplayFileName(string fileName) => fileName.Split('/', '\\').Last();

    internal async Task DeleteBestEffortAsync(
        IEnumerable<string> storagePaths,
        CancellationToken ct)
    {
        foreach (var storagePath in storagePaths.Distinct(StringComparer.Ordinal))
        {
            try
            {
                await fileStorage.DeleteAsync(storagePath, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to delete survey Information image {StoragePath}", storagePath);
            }
        }
    }

    internal sealed record PreparedInformationImages(
        SurveyEditInput Input,
        IReadOnlyList<string> NewStoragePaths,
        SurveyAuthoringError? Error = null);

    internal async Task<(SurveyEditInput Input, int Filled)> FillAsync(
        SurveyEditInput e, IReadOnlyList<string> targetCultures, CancellationToken ct)
    {
        var source = e.DefaultCulture;

        // Mutable copies of every authored LocalizedText, kept alongside what they rebuild into.
        var title = Copy(e.Title);
        var intro = Copy(e.Intro);
        var thankYou = Copy(e.ThankYou);
        var invitationEmailSubject = Copy(e.InvitationEmailSubject);
        var invitationEmailMessage = Copy(e.InvitationEmailMessage);
        var questions = e.Questions.Select(q => new
        {
            Question = q,
            Prompt = Copy(q.Prompt),
            Help = Copy(q.HelpText),
            MinLabel = Copy(q.RatingMinLabel),
            MaxLabel = Copy(q.RatingMaxLabel),
            Options = q.Options.Select(o => new { Option = o, Label = Copy(o.Label) }).ToList(),
            GridRows = (q.GridRows ?? []).Select(row => new { Row = row, Label = Copy(row.Label) }).ToList(),
            InformationImages = (q.InformationImages ?? []).Select(image => new
            {
                Image = image,
                Label = Copy(image.Label),
                AltText = Copy(image.AltText),
            }).ToList(),
        }).ToList();

        var allTexts = new List<Dictionary<string, string>>
        {
            title,
            intro,
            thankYou,
            invitationEmailSubject,
            invitationEmailMessage,
        };
        foreach (var q in questions)
        {
            allTexts.AddRange([q.Prompt, q.Help, q.MinLabel, q.MaxLabel]);
            allTexts.AddRange(q.Options.Select(o => o.Label));
            allTexts.AddRange(q.GridRows.Select(row => row.Label));
            allTexts.AddRange(q.InformationImages.Select(image => image.Label));
            allTexts.AddRange(q.InformationImages.Select(image => image.AltText));
        }

        // One batched call per target culture; only fills blanks — never overwrites authored text.
        var filled = 0;
        foreach (var target in targetCultures.Where(t => !string.Equals(t, source, StringComparison.OrdinalIgnoreCase)))
        {
            var pending = allTexts.Where(d => HasText(d, source) && !HasText(d, target)).ToList();
            if (pending.Count == 0) continue;

            var translated = await translation.TranslateAsync(
                pending.Select(d => d[source]).ToList(), source, target, ct);
            for (var i = 0; i < pending.Count; i++)
            {
                pending[i][target] = translated[i];
            }

            filled += pending.Count;
        }

        if (filled == 0) return (e, 0);

        var input = new SurveyEditInput(
            new LocalizedText(title), new LocalizedText(intro), new LocalizedText(thankYou),
            new LocalizedText(invitationEmailSubject), new LocalizedText(invitationEmailMessage),
            e.DefaultCulture, e.AllowAnonymous, e.OpensAt, e.ClosesAt,
            e.AudienceType, e.AudienceTeamId, e.AudienceLoggedInSince, e.PublicSlug,
            questions.Select(q => q.Question with
            {
                Prompt = new LocalizedText(q.Prompt),
                HelpText = new LocalizedText(q.Help),
                RatingMinLabel = new LocalizedText(q.MinLabel),
                RatingMaxLabel = new LocalizedText(q.MaxLabel),
                Options = q.Options.Select(o => o.Option with { Label = new LocalizedText(o.Label) }).ToList(),
                GridRows = q.GridRows.Select(row => row.Row with { Label = new LocalizedText(row.Label) }).ToList(),
                InformationImages = q.InformationImages.Select(image => image.Image with
                {
                    Label = new LocalizedText(image.Label),
                    AltText = new LocalizedText(image.AltText),
                }).ToList(),
            }).ToList(),
            e.IsAsociadoVote);

        return (input, filled);

        static Dictionary<string, string> Copy(LocalizedText text)
            => new(text.Values, StringComparer.OrdinalIgnoreCase);

        static bool HasText(Dictionary<string, string> values, string culture)
            => values.TryGetValue(culture, out var v) && !string.IsNullOrWhiteSpace(v);
    }
}
