using Humans.Base.Threading;
using Humans.Users.Contracts;
using Humans.Surveys.Data;
using Humans.Surveys.Contracts;
using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.Surveys.Domain;
using NodaTime;

namespace Humans.Surveys.Services;

internal sealed class SurveyResponseWorkflow(
    ISurveyRepository repo,
    IClock clock,
    ILogger<SurveyService> logger,
    IUserService userService,
    SurveyAudience audience,
    ISurveyInviteTokenProvider tokenProvider,
    SurveyDefinitionReader definitionReader) : IApplicationService
{
    private static readonly TrackedLock ResponseFinalizationLock = new("Surveys.ResponseFinalization");

    // A shared epoch, not the real time: a CompletionTracked public start's real CreatedAt would
    // correlate with the unlinked response's SubmittedAt and unmask the respondent.
    private static readonly Instant NonCorrelatablePublicParticipationCreatedAt =
        Instant.FromUtc(1970, 1, 1, 0, 0);

    internal async Task<SurveyAnswerContext?> ResolveAnswerContextAsync(string token, CancellationToken ct = default)
    {
        var invitationId = tokenProvider.Resolve(token);
        if (invitationId is null) return null;

        var invitation = await repo.GetInvitationByIdAsync(invitationId.Value, ct);
        if (invitation is null) return null;

        // A completed invitation's token is spent (Identified/CompletionTracked flip Completed at
        // submit) — resolving it again would let the same invite submit a second response.
        // Anonymous completions leave Completed false by design, so those tokens stay answerable.
        if (invitation.Completed) return null;

        var definition = await definitionReader.GetForEditAsync(invitation.SurveyId, ct);
        if (definition is null) return null;
        var isEligible = !definition.Editable.IsAsociadoVote
            || await IsEligibleAsociadoAsync(invitation.UserId, ct);

        var draft = isEligible
            ? await repo.GetDraftResponseAsync(invitation.SurveyId, invitation.UserId, ct)
            : null;
        var draftAnswers = draft is null ? [] : MapDraftAnswers(draft);

        return new SurveyAnswerContext(
            invitation.SurveyId,
            invitation.Id,
            invitation.UserId,
            definition,
            draftAnswers,
            DraftResponseId: draft?.Id,
            IsEligible: isEligible);
    }

    internal Task<bool> IsEligibleAsociadoAsync(Guid userId, CancellationToken ct = default) =>
        audience.IsEligibleAsociadoAsync(userId, ct);

    internal async Task<Guid> StartIdentifiedDraftAsync(
        Guid surveyId,
        Guid participationId,
        Guid userId,
        SurveyInputMethod inputMethod,
        string culture,
        CancellationToken ct = default)
        => (await GetOrCreateIdentifiedDraftAsync(
            surveyId, participationId, userId, inputMethod, culture, ct)).Id;

    private async Task<SurveyResponse> GetOrCreateIdentifiedDraftAsync(
        Guid surveyId,
        Guid participationId,
        Guid userId,
        SurveyInputMethod inputMethod,
        string culture,
        CancellationToken ct)
    {
        var survey = await repo.GetByIdAsync(surveyId, ct);
        if (survey?.IsAsociadoVote == true && !await IsEligibleAsociadoAsync(userId, ct))
        {
            throw new InvalidOperationException(
                "Only an active, approved Asociado may answer this vote.");
        }

        // Idempotent: one in-progress Identified draft per Human, regardless of entry path.
        using var finalization = await ResponseFinalizationLock.AcquireAsync(logger, ct);
        var existing = await repo.GetDraftResponseAsync(surveyId, userId, ct);
        if (existing is not null)
        {
            return existing;
        }

        var response = new SurveyResponse
        {
            Id = Guid.NewGuid(),
            SurveyId = surveyId,
            InvitationId = participationId,
            UserId = userId,
            Anonymity = ResponseAnonymity.Identified,
            InputMethod = inputMethod,
            Culture = culture,
            SubmittedAt = null,
            Answers = [],
        };

        // No audit log for individual response activity (privacy).
        await repo.AddResponseAsync(response, ct);
        return response;
    }

    internal async Task<SurveyPublicStart?> StartPublicTrackedResponseAsync(
        Guid surveyId,
        Guid userId,
        ResponseAnonymity anonymity,
        string culture,
        CancellationToken ct = default)
    {
        if (anonymity is not (ResponseAnonymity.Identified or ResponseAnonymity.CompletionTracked))
        {
            throw new ArgumentOutOfRangeException(nameof(anonymity), anonymity,
                "Only identified or completion-tracked public responses need a participation ledger.");
        }

        if (await HasCompletedMergedParticipationAsync(surveyId, userId, ct))
            return null;

        var createdAt = anonymity == ResponseAnonymity.CompletionTracked
            ? NonCorrelatablePublicParticipationCreatedAt
            : clock.GetCurrentInstant();
        var participation = await repo.GetOrCreateParticipationAsync(
            surveyId, userId, createdAt, ct);

        if (participation.Completed)
        {
            return null;
        }

        var draft = anonymity == ResponseAnonymity.Identified
            ? await GetOrCreateIdentifiedDraftAsync(
                surveyId, participation.Id, userId, SurveyInputMethod.Slug, culture, ct)
            : await repo.GetDraftResponseAsync(surveyId, userId, ct);
        return new SurveyPublicStart(
            participation.Id,
            anonymity == ResponseAnonymity.Identified ? draft!.Id : null,
            draft is null ? [] : MapDraftAnswers(draft));
    }

    internal Task MarkInvitationStartedAsync(Guid invitationId, CancellationToken ct = default)
        => repo.MarkInvitationStartedAsync(invitationId, ct);

    internal async Task<SurveyPublicContext?> ResolvePublicContextAsync(
        string slug,
        Guid? userId,
        CancellationToken ct = default)
    {
        // Invalid public URLs are lookup misses; authoring rejects invalid slugs instead.
        var (normalized, slugError) = SurveyDefinitionValidation.PreparePublicSlugForWrite(slug);
        if (normalized is null || slugError is not null) return null;

        var surveyId = await repo.GetIdByPublicSlugAsync(normalized, ct);
        if (surveyId is null) return null;

        var definition = await definitionReader.GetForEditAsync(surveyId.Value, ct);
        if (definition is null) return null;

        var editable = definition.Editable;
        if (editable.AllowAnonymous)
            return new SurveyPublicContext(surveyId.Value, definition);

        if (userId is null)
        {
            return new SurveyPublicContext(
                surveyId.Value, definition, SurveyPublicAccess.AuthenticationRequired);
        }

        var isEligible = editable.AudienceType is null
            || (await audience.ResolveRecipientIdsAsync(
                editable.AudienceType.Value,
                editable.AudienceTeamId,
                editable.AudienceLoggedInSince,
                ct)).Contains(userId.Value);

        return new SurveyPublicContext(
            surveyId.Value,
            definition,
            isEligible ? SurveyPublicAccess.Allowed : SurveyPublicAccess.Ineligible);
    }

    internal Task IncrementPublicStartedAsync(Guid surveyId, CancellationToken ct = default)
        => repo.IncrementPublicStartedAsync(surveyId, ct);

    internal async Task SaveDraftAnswersAsync(
        Guid draftResponseId,
        IReadOnlyList<SurveyAnswerInput> answers,
        SurveyInputMethod inputMethod,
        string culture,
        CancellationToken ct = default)
    {
        using var finalization = await ResponseFinalizationLock.AcquireAsync(logger, ct);
        await repo.SaveDraftAnswersAsync(
            draftResponseId,
            MapAnswers(draftResponseId, answers),
            inputMethod,
            culture,
            ct);
    }

    private async Task<bool> HasCompletedMergedParticipationAsync(Guid surveyId, Guid userId, CancellationToken ct)
    {
        var human = await userService.GetUserInfoAsync(userId, ct);
        if (human is null || (human.Id == userId && human.MergedUserIds.Count == 0))
            return false;

        // Frozen participation rows retain the old account id; the completion rule belongs to the human.
        var ids = human.AllUserIds.Append(userId).ToHashSet();
        return (await repo.GetInvitationsAsync(surveyId, ct))
            .Any(invitation => invitation.Completed && ids.Contains(invitation.UserId));
    }

    private async Task<SubmissionPreparation> PrepareSubmissionAsync(
        SurveySubmission submission, CancellationToken ct)
    {
        // Load one authoritative definition for branching, normalization, required validation, and
        // persistence preparation. The caller must persist the returned answers without reloading it.
        var survey = await repo.GetByIdAsync(submission.SurveyId, ct);
        if (survey is null)
            return new([], [], [], [], AlreadyCompleted: false, Refusal: SurveyWizardOutcome.NotFound);

        // The service is the authoritative gate: the controller's window checks are UX, this one is the rule.
        if (!SurveyWizardFlow.IsAnswerable(survey.Status, survey.OpensAt, survey.ClosesAt, clock.GetCurrentInstant()))
        {
            return new([], [], [], [], AlreadyCompleted: false, Refusal: SurveyWizardOutcome.Closed);
        }
        if (survey.IsAsociadoVote == true)
        {
            if (submission.Anonymity != ResponseAnonymity.CompletionTracked
                || submission.UserId is not { } userId
                || submission.InvitationId is null)
            {
                return new([], [], [], [], AlreadyCompleted: false, Refusal: SurveyWizardOutcome.Ineligible);
            }
            if (!await IsEligibleAsociadoAsync(userId, ct))
            {
                return new([], [], [], [], AlreadyCompleted: false, Refusal: SurveyWizardOutcome.Ineligible);
            }
        }

        // Same authoritative posture for the duplicate gate: a completed invitation can't submit
        // again on the tracked tiers (Anonymous never flips Completed, so it is unaffected).
        if (submission.Anonymity != ResponseAnonymity.Anonymous && submission.InvitationId is { } gateInvId)
        {
            var invitation = await repo.GetInvitationByIdAsync(gateInvId, ct);
            if (invitation?.Completed == true
                || (submission.UserId is { } participantId
                    && await HasCompletedMergedParticipationAsync(submission.SurveyId, participantId, ct)))
            {
                return new SubmissionPreparation([], [], [], [], AlreadyCompleted: true);
            }
        }

        // Drop answers to questions hidden under full branching (defends against tampered/stale posts).
        var visible = VisibleAnswers(survey, submission.Answers);
        var visibleAnswers = visible.Answers;
        var questions = SurveyQuestionMapping.ToQuestionInputs(survey);
        var answerStates = visibleAnswers.ToDictionary(
            answer => answer.QuestionId,
            answer => new AnswerState(
                answer.SelectedOptionValues,
                answer.TextValue,
                answer.RatingValue,
                answer.GridSelections,
                answer.RankedValue));
        var allVisible = SurveyWizardFlow.OrderedPages(questions)
            .SelectMany(page => SurveyWizardFlow.VisibleQuestionsOnPage(questions, page, answerStates))
            .ToList();
        var missingRequired = SurveyWizardFlow.RequiredUnanswered(allVisible, answerStates);

        return new SubmissionPreparation(
            visibleAnswers,
            questions,
            missingRequired,
            visible.InvalidAnswers,
            AlreadyCompleted: false);
    }

    private async Task PersistResponseAsync(
        SurveySubmission submission,
        IReadOnlyList<SurveyAnswerInput> visibleAnswers,
        CancellationToken ct)
    {
        using var finalization = await ResponseFinalizationLock.AcquireAsync(logger, ct);
        // Another account in the same merge chain may have completed since preparation. Keep the
        // duplicate check inside the same lock as persistence, without linking anonymous ballots.
        if (submission.Anonymity != ResponseAnonymity.Anonymous && submission.UserId is { } participantId
            && await HasCompletedMergedParticipationAsync(submission.SurveyId, participantId, ct))
            return;

        switch (submission.Anonymity)
        {
            case ResponseAnonymity.Identified:
                {
                    var now = clock.GetCurrentInstant();
                    var invitationId = submission.InvitationId
                        ?? throw new InvalidOperationException("Identified responses require a participation identity.");
                    var draftId = submission.DraftResponseId
                        ?? throw new InvalidOperationException("Identified responses require an active draft.");
                    await repo.FinalizeIdentifiedResponseAsync(
                        invitationId,
                        draftId,
                        MapAnswers(draftId, visibleAnswers),
                        now,
                        submission.InputMethod,
                        submission.Culture,
                        ct);

                    break;
                }

            case ResponseAnonymity.CompletionTracked:
                {
                    var responseId = Guid.NewGuid();
                    var response = new SurveyResponse
                    {
                        Id = responseId,
                        SurveyId = submission.SurveyId,
                        // No link stored on the response — only the invitation's Completed flag is flipped.
                        InvitationId = null,
                        UserId = null,
                        Anonymity = ResponseAnonymity.CompletionTracked,
                        InputMethod = submission.InputMethod,
                        Culture = submission.Culture,
                        SubmittedAt = clock.GetCurrentInstant(),
                        Answers = MapAnswers(responseId, visibleAnswers),
                    };
                    var invitationId = submission.InvitationId
                        ?? throw new InvalidOperationException(
                            "Completion-tracked responses require a participation identity.");
                    var userId = submission.UserId
                        ?? throw new InvalidOperationException(
                            "Completion-tracked responses require a participation identity.");
                    await repo.FinalizeCompletionTrackedResponseAsync(
                        invitationId, userId, response, ct);

                    break;
                }

            case ResponseAnonymity.Anonymous:
            default:
                {
                    var responseId = Guid.NewGuid();
                    var response = new SurveyResponse
                    {
                        Id = responseId,
                        SurveyId = submission.SurveyId,
                        InvitationId = null,
                        UserId = null,
                        Anonymity = ResponseAnonymity.Anonymous,
                        InputMethod = submission.InputMethod,
                        Culture = submission.Culture,
                        SubmittedAt = clock.GetCurrentInstant(),
                        Answers = MapAnswers(responseId, visibleAnswers),
                    };
                    // Anonymous leaves the invitation's Completed flag untouched (no link, even to participation).
                    await repo.AddResponseWithAnswersAndSaveAsync(response, ct);
                    break;
                }
        }
    }

    /// <summary>
    /// One wizard step (see <see cref="ISurveyService.AdvanceWizardAsync"/>): capture → first-advance
    /// funnel side effect → Identified autosave → required-validation → back/next navigation or submit.
    /// All flow decisions live here; the controller only persists the session and renders the outcome.
    /// </summary>
    internal async Task<SurveyWizardAdvanceResult> AdvanceWizardAsync(
        SurveyWizardState state, int page, bool back, IReadOnlyList<SurveyAnswerInput> postedAnswers,
        CancellationToken ct = default)
    {
        var definition = await definitionReader.GetForEditAsync(state.SurveyId, ct);
        if (definition is null) return new SurveyWizardAdvanceResult(SurveyWizardOutcome.NotFound, []);

        var editable = definition.Editable;
        if (!SurveyWizardFlow.IsAnswerable(definition.Status, editable.OpensAt, editable.ClosesAt, clock.GetCurrentInstant()))
        {
            return new SurveyWizardAdvanceResult(SurveyWizardOutcome.Closed, []);
        }
        if (editable.IsAsociadoVote
            && (state.UserId is not { } userId || !await IsEligibleAsociadoAsync(userId, ct)))
        {
            return new SurveyWizardAdvanceResult(SurveyWizardOutcome.Ineligible, []);
        }

        // Only accept answers for questions actually visible on the posted page (re-evaluated server-side).
        var visibleBefore = SurveyWizardFlow.VisibleQuestionsOnPage(
            editable.Questions, page, SurveyWizardFlow.ToAnswerStates(state.Answers));
        var posted = postedAnswers.ToLookup(a => a.QuestionId);
        var invalidAnswers = new List<Guid>();

        foreach (var question in visibleBefore)
        {
            if (question.Type == SurveyQuestionType.Information) continue;
            var id = question.Id!.Value;
            var questionAnswers = posted[id];
            if (questionAnswers.Count() > 1)
            {
                logger.LogWarning("Rejected duplicate wizard answers for survey {SurveyId} question {QuestionId}", state.SurveyId, id);
                invalidAnswers.Add(id);
                state.Answers.Remove(id.ToString());
                continue;
            }
            var answer = questionAnswers.FirstOrDefault();
            if (answer is null)
            {
                state.Answers.Remove(id.ToString());
                continue;
            }

            var normalized = SurveyAnswerNormalization.NormalizeAnswer(question, answer);
            answer = normalized.Answer;
            if (normalized.Refusal is { } refusal)
            {
                logger.LogWarning(
                    "Rejected wizard answer for survey {SurveyId} question {QuestionId}: {Reason}",
                    state.SurveyId, id, refusal);
                invalidAnswers.Add(id);
            }

            state.Answers[id.ToString()] = new SurveyWizardAnswer
            {
                SelectedOptionValues = answer.SelectedOptionValues.ToList(),
                GridSelections = answer.GridSelections?.ToDictionary(
                    pair => pair.Key, pair => pair.Value.ToList(), StringComparer.Ordinal)
                    ?? new Dictionary<string, List<string>>(StringComparer.Ordinal),
                TextValue = answer.TextValue,
                RatingValue = answer.RatingValue,
                RankedValue = answer.RankedValue,
            };
        }

        if (invalidAnswers.Count > 0)
        {
            state.CurrentPage = page;
            return new SurveyWizardAdvanceResult(
                SurveyWizardOutcome.ValidationFailed,
                [],
                invalidAnswers);
        }

        // A survey may be edited while a respondent has a wizard session open. Re-normalize every
        // stored Grid answer against the current schema before autosave, validation, navigation, or
        // submission so removed cells cannot survive and newly required rows cannot be bypassed.
        foreach (var question in editable.Questions.Where(q => q.Type == SurveyQuestionType.Grid && q.Id is not null))
        {
            if (!state.Answers.TryGetValue(question.Id!.Value.ToString(), out var answer)) continue;
            answer.GridSelections = SurveyAnswerNormalization.NormalizeGridSelections(
                question.GridRows,
                question.Options,
                question.GridSelectionMode,
                answer.GridSelections.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<string>)pair.Value,
                    StringComparer.Ordinal));
        }

        // First advance past the intro fires the path-specific Started funnel side effect (idempotent via state.Started).
        if (!state.Started)
        {
            if (state.InputMethod == SurveyInputMethod.Slug)
            {
                await repo.IncrementPublicStartedAsync(state.SurveyId, ct);
            }
            else if (state.InvitationId is { } startInvId)
            {
                await repo.MarkInvitationStartedAsync(startInvId, ct);
            }

            state.Started = true;
        }

        var answerStates = SurveyWizardFlow.ToAnswerStates(state.Answers);

        // Identified per-page autosave (replace-all; the draft stays in-progress) on either entry path.
        if (state.Anonymity == ResponseAnonymity.Identified && state.DraftResponseId is { } draftId)
        {
            await SaveDraftAnswersAsync(
                draftId,
                SurveyWizardFlow.ToAnswerInputs(state.Answers),
                state.InputMethod,
                state.Culture,
                ct);
        }

        // Re-validate required-visible on this page; a Back navigation skips validation.
        if (!back)
        {
            var visibleAfter = SurveyWizardFlow.VisibleQuestionsOnPage(editable.Questions, page, answerStates);
            var missing = SurveyWizardFlow.RequiredUnanswered(visibleAfter, answerStates);
            if (missing.Count > 0)
            {
                state.CurrentPage = page;
                return new SurveyWizardAdvanceResult(SurveyWizardOutcome.ValidationFailed, missing);
            }
        }

        if (back)
        {
            state.CurrentPage = SurveyWizardFlow.PreviousVisiblePage(editable.Questions, page, answerStates) ?? page;
            return new SurveyWizardAdvanceResult(SurveyWizardOutcome.Navigated, []);
        }

        var nextPage = SurveyWizardFlow.NextVisiblePage(editable.Questions, page, answerStates);
        if (nextPage is not null)
        {
            state.CurrentPage = nextPage.Value;
            return new SurveyWizardAdvanceResult(SurveyWizardOutcome.Navigated, []);
        }

        // The current page has passed validation, but an author may have changed an earlier page
        // while this session was in progress. Revalidate every currently visible required question
        // immediately before final submission and return to the first page that needs attention.
        var allVisible = SurveyWizardFlow.OrderedPages(editable.Questions)
            .SelectMany(visiblePage => SurveyWizardFlow.VisibleQuestionsOnPage(
                editable.Questions, visiblePage, answerStates))
            .ToList();
        var missingBeforeSubmit = SurveyWizardFlow.RequiredUnanswered(allVisible, answerStates);
        if (missingBeforeSubmit.Count > 0)
        {
            var firstMissing = missingBeforeSubmit.ToHashSet();
            state.CurrentPage = allVisible.First(q => q.Id is { } id && firstMissing.Contains(id)).PageNumber;
            return new SurveyWizardAdvanceResult(SurveyWizardOutcome.ValidationFailed, missingBeforeSubmit);
        }

        // Reload once at the submission boundary, then validate and persist against that same
        // authoritative definition. If the schema changed after the wizard validation above,
        // return the respondent to the first newly incomplete required question.
        var submission = new SurveySubmission(
            state.SurveyId,
            state.InvitationId,
            state.Anonymity == ResponseAnonymity.Anonymous ? null : state.UserId,
            state.DraftResponseId,
            state.Anonymity,
            state.InputMethod,
            state.Culture,
            SurveyWizardFlow.ToAnswerInputs(state.Answers));
        var prepared = await PrepareSubmissionAsync(submission, ct);
        if (prepared.Refusal is { } submissionRefusal)
        {
            logger.LogWarning("Survey final submission refused for {SurveyId}: {Outcome}", state.SurveyId, submissionRefusal);
            return new SurveyWizardAdvanceResult(submissionRefusal, []);
        }
        if (prepared.AlreadyCompleted)
        {
            return new SurveyWizardAdvanceResult(SurveyWizardOutcome.Submitted, []);
        }
        if (prepared.InvalidAnswers.Count > 0)
        {
            ReplaceWizardAnswers(state, prepared.VisibleAnswers);
            var invalidIds = prepared.InvalidAnswers.ToHashSet();
            state.CurrentPage = prepared.Questions
                .Where(question => question.Id is { } id && invalidIds.Contains(id))
                .Min(question => question.PageNumber);
            return new SurveyWizardAdvanceResult(
                SurveyWizardOutcome.ValidationFailed,
                [],
                prepared.InvalidAnswers);
        }
        if (prepared.MissingRequired.Count > 0)
        {
            ReplaceWizardAnswers(state, prepared.VisibleAnswers);
            var missingIds = prepared.MissingRequired.ToHashSet();
            state.CurrentPage = prepared.Questions
                .Where(question => question.Id is { } id && missingIds.Contains(id))
                .Min(question => question.PageNumber);
            return new SurveyWizardAdvanceResult(
                SurveyWizardOutcome.ValidationFailed,
                prepared.MissingRequired);
        }

        // No further visible page ⇒ submit. Identity columns are written only for Identified.
        await PersistResponseAsync(submission, prepared.VisibleAnswers, ct);

        return new SurveyWizardAdvanceResult(SurveyWizardOutcome.Submitted, []);
    }

    private static void ReplaceWizardAnswers(
        SurveyWizardState state,
        IReadOnlyList<SurveyAnswerInput> answers)
    {
        state.Answers.Clear();
        foreach (var answer in answers)
        {
            state.Answers[answer.QuestionId.ToString()] = new SurveyWizardAnswer
            {
                SelectedOptionValues = answer.SelectedOptionValues.ToList(),
                GridSelections = answer.GridSelections?.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value.ToList(),
                        StringComparer.Ordinal)
                    ?? new Dictionary<string, List<string>>(StringComparer.Ordinal),
                TextValue = answer.TextValue,
                RatingValue = answer.RatingValue,
                RankedValue = answer.RankedValue,
            };
        }
    }

    /// <summary>
    /// Keeps only the answers to questions visible under full cascading branching: an answer on a
    /// hidden question neither survives nor counts towards downstream <c>ShowIf</c> conditions.
    /// </summary>
    private VisibleAnswerPreparation VisibleAnswers(
        Survey survey,
        IReadOnlyList<SurveyAnswerInput> answers)
    {
        var questions = SurveyQuestionMapping.ToQuestionInputs(survey).ToDictionary(q => q.Id!.Value);
        var invalidAnswers = new HashSet<Guid>();
        var normalizedAnswers = answers
            .Where(a => questions.TryGetValue(a.QuestionId, out var question)
                && question.Type != SurveyQuestionType.Information)
            .Select(a =>
            {
                var normalized = SurveyAnswerNormalization.NormalizeAnswer(questions[a.QuestionId], a);
                if (normalized.Refusal is { } refusal)
                {
                    logger.LogWarning(
                        "Rejected answer for survey {SurveyId} question {QuestionId}: {Reason}",
                        survey.Id, a.QuestionId, refusal);
                    invalidAnswers.Add(a.QuestionId);
                }
                return normalized.Answer;
            })
            .ToList();
        // Invalid answers cannot drive branching; an invalid answer that is itself hidden is ignored.
        var states = normalizedAnswers.ToDictionary(
            a => a.QuestionId,
            a => invalidAnswers.Contains(a.QuestionId)
                ? AnswerState.None
                : new AnswerState(a.SelectedOptionValues, a.TextValue, a.RatingValue, a.GridSelections, a.RankedValue));
        var effective = SurveyBranchingEvaluator.EffectiveAnswerStates(
            survey.Questions
                .OrderBy(q => q.PageNumber).ThenBy(q => q.Order)
                .Select(q => (q.Id, q.ShowIf)),
            states);
        var visibleAnswers = normalizedAnswers.Where(a => effective.ContainsKey(a.QuestionId)).ToList();
        return new VisibleAnswerPreparation(
            visibleAnswers,
            visibleAnswers.Where(a => invalidAnswers.Contains(a.QuestionId)).Select(a => a.QuestionId).ToList());
    }

    private sealed record VisibleAnswerPreparation(
        IReadOnlyList<SurveyAnswerInput> Answers,
        IReadOnlyList<Guid> InvalidAnswers);

    private sealed record SubmissionPreparation(
        IReadOnlyList<SurveyAnswerInput> VisibleAnswers,
        IReadOnlyList<QuestionInput> Questions,
        IReadOnlyList<Guid> MissingRequired,
        IReadOnlyList<Guid> InvalidAnswers,
        bool AlreadyCompleted,
        SurveyWizardOutcome? Refusal = null);

    private static List<SurveyAnswer> MapAnswers(Guid responseId, IReadOnlyList<SurveyAnswerInput> answers)
        => answers.Select(a => new SurveyAnswer
        {
            Id = Guid.NewGuid(),
            ResponseId = responseId,
            QuestionId = a.QuestionId,
            SelectedOptionValues = a.SelectedOptionValues.ToList(),
            GridSelections = a.GridSelections?.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ToList(),
                StringComparer.Ordinal),
            TextValue = a.TextValue,
            RatingValue = a.RatingValue,
            RankedValue = a.RankedValue,
        }).ToList();

    private static IReadOnlyList<SurveyDraftAnswer> MapDraftAnswers(SurveyResponse draft)
        => draft.Answers
            .Select(answer => new SurveyDraftAnswer(
                answer.QuestionId,
                answer.SelectedOptionValues,
                answer.TextValue,
                answer.RatingValue,
                answer.GridSelections?.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<string>)pair.Value,
                    StringComparer.Ordinal),
                answer.RankedValue))
            .ToList();

}
