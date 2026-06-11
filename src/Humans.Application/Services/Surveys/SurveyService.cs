using Humans.Application.Extensions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Surveys;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Surveys;

/// <summary>
/// Application-layer <see cref="ISurveyService"/>. Plain Scoped service (no caching decorator, per
/// spec §12). Cross-domain display data is stitched from <c>I…ServiceRead</c> interfaces — the
/// repository never resolves user/team navs. Authoring lives here; send/submit/results/export/GDPR
/// are added in their phases (constructor grows with them).
/// </summary>
public sealed class SurveyService(
    ISurveyRepository repo,
    IAuditLogService auditLog,
    IClock clock,
    ILogger<SurveyService> logger,
    ITeamServiceRead teamService,
    IUserServiceRead userService,
    ITicketServiceRead ticketService,
    IShiftView shiftView,
    IUserEmailService userEmailService,
    IEmailService emailService,
    IEmailMessageFactory emailMessages,
    ISurveyInviteTokenProvider tokenProvider,
    IGoogleTranslationService translation) : ISurveyService, IUserDataContributor
{
    public async Task<IReadOnlyList<SurveySummary>> GetSummariesAsync(CancellationToken ct = default)
    {
        var surveys = await repo.GetAllSummariesAsync(ct);
        var invited = await repo.GetInvitedCountsBySurveyAsync(ct);
        var responses = await repo.GetResponseCountsBySurveyAsync(ct);

        return surveys.Select(s => new SurveySummary(
            s.Id,
            s.Title.Resolve(s.DefaultCulture, s.DefaultCulture),
            s.Status,
            invited.GetValueOrDefault(s.Id),
            responses.GetValueOrDefault(s.Id))).ToList();
    }

    public async Task<SurveyDetail?> GetForEditAsync(Guid surveyId, CancellationToken ct = default)
    {
        var s = await repo.GetByIdAsync(surveyId, ct);
        if (s is null) return null;

        var input = new SurveyEditInput(
            s.Title, s.Intro, s.ThankYou, s.DefaultCulture, s.AllowAnonymous, s.OpensAt, s.ClosesAt,
            s.AudienceType, s.AudienceTeamId, s.PublicSlug,
            s.Questions
                .OrderBy(q => q.PageNumber).ThenBy(q => q.Order)
                .Select(q => new QuestionInput(
                    q.Id, q.PageNumber, q.Order, q.Type, q.Prompt, q.HelpText, q.IsRequired,
                    q.RatingMin, q.RatingMax, q.RatingMinLabel, q.RatingMaxLabel, q.ShowIf,
                    q.Options.OrderBy(o => o.Order)
                        .Select(o => new OptionInput(o.Id, o.Order, o.Value, o.Label)).ToList()))
                .ToList());

        return new SurveyDetail(s.Id, s.Status, input);
    }

    public async Task<Guid> CreateAsync(SurveyEditInput input, Guid actorUserId, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var surveyId = Guid.NewGuid();
        var questions = MapQuestions(surveyId, input);
        ValidateBranching(questions);

        var survey = new Survey
        {
            Id = surveyId,
            Title = input.Title,
            Intro = input.Intro,
            ThankYou = input.ThankYou,
            DefaultCulture = input.DefaultCulture,
            AllowAnonymous = input.AllowAnonymous,
            Status = SurveyStatus.Draft,
            OpensAt = input.OpensAt,
            ClosesAt = input.ClosesAt,
            AudienceType = input.AudienceType,
            AudienceTeamId = input.AudienceTeamId,
            PublicSlug = NormalizeSlug(input.PublicSlug),
            CreatedByUserId = actorUserId,
            CreatedAt = now,
            UpdatedAt = now,
            Questions = questions,
        };

        await repo.AddAsync(survey, ct);
        logger.LogInformation("Survey {SurveyId} created by {UserId}", surveyId, actorUserId);
        await auditLog.LogAsync(AuditAction.SurveyCreated, nameof(Survey), surveyId,
            $"Created survey '{survey.Title.Resolve(survey.DefaultCulture, survey.DefaultCulture)}'", actorUserId);
        return surveyId;
    }

    public async Task UpdateAsync(Guid surveyId, SurveyEditInput input, Guid actorUserId, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var questions = MapQuestions(surveyId, input);
        ValidateBranching(questions);

        var survey = new Survey
        {
            Id = surveyId,
            Title = input.Title,
            Intro = input.Intro,
            ThankYou = input.ThankYou,
            DefaultCulture = input.DefaultCulture,
            AllowAnonymous = input.AllowAnonymous,
            OpensAt = input.OpensAt,
            ClosesAt = input.ClosesAt,
            AudienceType = input.AudienceType,
            AudienceTeamId = input.AudienceTeamId,
            PublicSlug = NormalizeSlug(input.PublicSlug),
            UpdatedAt = now,
            Questions = questions,
        };

        await repo.UpdateAsync(survey, ct);
        await auditLog.LogAsync(AuditAction.SurveyUpdated, nameof(Survey), surveyId, "Updated survey", actorUserId);
    }

    public async Task<int> PreFillTranslationsAsync(
        Guid surveyId, IReadOnlyList<string> targetCultures, Guid actorUserId, CancellationToken ct = default)
    {
        var detail = await GetForEditAsync(surveyId, ct)
            ?? throw new InvalidOperationException("Survey not found.");
        var e = detail.Editable;
        var source = e.DefaultCulture;

        // Mutable copies of every authored LocalizedText, kept alongside what they rebuild into.
        var title = Copy(e.Title);
        var intro = Copy(e.Intro);
        var thankYou = Copy(e.ThankYou);
        var questions = e.Questions.Select(q => new
        {
            Question = q,
            Prompt = Copy(q.Prompt),
            Help = Copy(q.HelpText),
            MinLabel = Copy(q.RatingMinLabel),
            MaxLabel = Copy(q.RatingMaxLabel),
            Options = q.Options.Select(o => new { Option = o, Label = Copy(o.Label) }).ToList(),
        }).ToList();

        var allTexts = new List<Dictionary<string, string>> { title, intro, thankYou };
        foreach (var q in questions)
        {
            allTexts.AddRange([q.Prompt, q.Help, q.MinLabel, q.MaxLabel]);
            allTexts.AddRange(q.Options.Select(o => o.Label));
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

        if (filled == 0) return 0;

        var input = new SurveyEditInput(
            new LocalizedText(title), new LocalizedText(intro), new LocalizedText(thankYou),
            e.DefaultCulture, e.AllowAnonymous, e.OpensAt, e.ClosesAt,
            e.AudienceType, e.AudienceTeamId, e.PublicSlug,
            questions.Select(q => q.Question with
            {
                Prompt = new LocalizedText(q.Prompt),
                HelpText = new LocalizedText(q.Help),
                RatingMinLabel = new LocalizedText(q.MinLabel),
                RatingMaxLabel = new LocalizedText(q.MaxLabel),
                Options = q.Options.Select(o => o.Option with { Label = new LocalizedText(o.Label) }).ToList(),
            }).ToList());

        await UpdateAsync(surveyId, input, actorUserId, ct);
        logger.LogInformation(
            "Survey {SurveyId}: pre-filled {Count} missing translations from {Source}", surveyId, filled, source);
        return filled;

        static Dictionary<string, string> Copy(LocalizedText text)
            => new(text.Values, StringComparer.Ordinal);

        static bool HasText(Dictionary<string, string> values, string culture)
            => values.TryGetValue(culture, out var v) && !string.IsNullOrWhiteSpace(v);
    }

    public async Task OpenAsync(Guid surveyId, Guid actorUserId, CancellationToken ct = default)
    {
        var status = await repo.GetStatusAsync(surveyId, ct)
            ?? throw new InvalidOperationException("Survey not found.");
        if (status == SurveyStatus.Open) return;

        await repo.SetStatusAsync(surveyId, SurveyStatus.Open, clock.GetCurrentInstant(), ct);
        await auditLog.LogAsync(AuditAction.SurveyOpened, nameof(Survey), surveyId, "Opened survey", actorUserId);
    }

    public async Task CloseAsync(Guid surveyId, Guid actorUserId, CancellationToken ct = default)
    {
        var status = await repo.GetStatusAsync(surveyId, ct)
            ?? throw new InvalidOperationException("Survey not found.");
        if (status == SurveyStatus.Closed) return;

        await repo.SetStatusAsync(surveyId, SurveyStatus.Closed, clock.GetCurrentInstant(), ct);
        await auditLog.LogAsync(AuditAction.SurveyClosed, nameof(Survey), surveyId, "Closed survey", actorUserId);
    }

    public async Task<int> PreviewAudienceCountAsync(Guid surveyId, CancellationToken ct = default)
    {
        var survey = await repo.GetByIdAsync(surveyId, ct);
        if (survey?.AudienceType is null) return 0;

        var recipients = await ResolveRecipientIdsAsync(survey.AudienceType.Value, survey.AudienceTeamId, ct);
        return recipients.Count;
    }

    public async Task<SendResult> SendInvitesAsync(Guid surveyId, Guid actorUserId, CancellationToken ct = default)
    {
        var survey = await repo.GetByIdAsync(surveyId, ct)
            ?? throw new InvalidOperationException("Survey not found.");
        if (survey.Status != SurveyStatus.Open)
            throw new InvalidOperationException("Invitations can only be sent for an Open survey.");
        if (survey.AudienceType is null)
            throw new InvalidOperationException("Survey has no audience to invite.");

        var now = clock.GetCurrentInstant();
        var target = await ResolveRecipientIdsAsync(survey.AudienceType.Value, survey.AudienceTeamId, ct);
        var alreadyInvited = await repo.GetInvitedUserIdsAsync(surveyId, ct);
        var netNew = target.Except(alreadyInvited).ToList();

        var emails = await userEmailService.GetNotificationTargetEmailsAsync(netNew, ct);
        var users = await userService.GetUserInfosAsync(netNew, ct);
        var title = survey.Title.Resolve(survey.DefaultCulture, survey.DefaultCulture);

        var invitationsCreated = 0;
        var emailsQueued = 0;
        var failed = 0;

        foreach (var userId in netNew)
        {
            if (!emails.TryGetValue(userId, out var email))
            {
                logger.LogWarning(
                    "User {UserId} has no notification email for survey {SurveyId}; skipping invitation",
                    userId, surveyId);
                continue;
            }

            var inv = new SurveyInvitation
            {
                Id = Guid.NewGuid(),
                SurveyId = surveyId,
                UserId = userId,
                SentAt = now,
                LatestEmailStatus = EmailOutboxStatus.Queued,
                CreatedAt = now,
            };
            await repo.AddInvitationAndSaveAsync(inv, ct);
            invitationsCreated++;

            var culture = users.TryGetValue(userId, out var user) ? user.PreferredLanguage : survey.DefaultCulture;
            var name = user?.BurnerName ?? string.Empty;
            var token = tokenProvider.Create(inv.Id);
            var msg = emailMessages.SurveyInvitation(email, name, title, token, culture);

            try
            {
                await emailService.SendAsync(msg, ct);
                emailsQueued++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to enqueue survey invitation email for user {UserId} invitation {InvitationId} in survey {SurveyId}",
                    userId, inv.Id, surveyId);
                await repo.UpdateInvitationStatusAsync(inv.Id, EmailOutboxStatus.Failed, now, ct);
                failed++;
            }
        }

        await auditLog.LogAsync(AuditAction.SurveyInvitesSent, nameof(Survey), surveyId,
            $"Sent {invitationsCreated} invitation(s)", actorUserId);

        return new SendResult(invitationsCreated, emailsQueued, failed);
    }

    public async Task<int> SendDueRemindersAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var cutoff = now - Duration.FromDays(7);

        var due = await repo.GetInvitationsDueForReminderAsync(cutoff, ct);
        if (due.Count == 0) return 0;

        // Resolve emails + display info for the whole due set in one cross-section call each.
        var userIds = due.Select(i => i.UserId).Distinct().ToList();
        var emails = await userEmailService.GetNotificationTargetEmailsAsync(userIds, ct);
        var users = await userService.GetUserInfosAsync(userIds, ct);

        // Title + default culture loaded once per distinct survey (few open surveys at this scale).
        var titles = new Dictionary<Guid, (string Title, string DefaultCulture)>();

        var reminded = 0;
        foreach (var inv in due)
        {
            if (!emails.TryGetValue(inv.UserId, out var email))
            {
                logger.LogWarning(
                    "User {UserId} has no notification email for survey {SurveyId}; skipping reminder",
                    inv.UserId, inv.SurveyId);
                continue;
            }

            if (!titles.TryGetValue(inv.SurveyId, out var meta))
            {
                var survey = await repo.GetByIdAsync(inv.SurveyId, ct);
                if (survey is null) continue;
                meta = (survey.Title.Resolve(survey.DefaultCulture, survey.DefaultCulture), survey.DefaultCulture);
                titles[inv.SurveyId] = meta;
            }

            var culture = users.TryGetValue(inv.UserId, out var user) ? user.PreferredLanguage : meta.DefaultCulture;
            var name = user?.BurnerName ?? string.Empty;
            var token = tokenProvider.Create(inv.Id);
            var msg = emailMessages.SurveyReminder(email, name, meta.Title, token, culture);

            // Per-invitee guard (mirrors SendInvitesAsync): one transport failure must not abort the
            // sweep. ReminderSentAt stays unstamped on failure so the next daily run retries.
            try
            {
                await emailService.SendAsync(msg, ct);
                await repo.SetReminderSentAsync(inv.Id, now, ct);
                reminded++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to enqueue survey reminder email for user {UserId} invitation {InvitationId} in survey {SurveyId}",
                    inv.UserId, inv.Id, inv.SurveyId);
            }
        }

        await auditLog.LogAsync(AuditAction.SurveyReminderSent, nameof(Survey), Guid.Empty,
            $"Sent {reminded} survey reminder(s)", jobName: nameof(SurveyService));

        return reminded;
    }

    public async Task<IReadOnlyList<SurveyInviteStatus>> GetInviteStatusesAsync(Guid surveyId, CancellationToken ct = default)
    {
        var invitations = await repo.GetInvitationsAsync(surveyId, ct);
        if (invitations.Count == 0) return [];

        var userIds = invitations.Select(i => i.UserId).Distinct().ToList();
        var users = await userService.GetUserInfosAsync(userIds, ct);

        return invitations.Select(i => new SurveyInviteStatus(
            i.UserId,
            users.TryGetValue(i.UserId, out var user) ? user.BurnerName : i.UserId.ToString(),
            i.LatestEmailStatus,
            i.Completed,
            i.Started,
            i.SentAt,
            i.ReminderSentAt)).ToList();
    }

    public async Task<SurveyAnswerContext?> ResolveAnswerContextAsync(string token, CancellationToken ct = default)
    {
        var invitationId = tokenProvider.Resolve(token);
        if (invitationId is null) return null;

        var invitation = await repo.GetInvitationByIdAsync(invitationId.Value, ct);
        if (invitation is null) return null;

        // A completed invitation's token is spent (Identified/CompletionTracked flip Completed at
        // submit) — resolving it again would let the same invite submit a second response.
        // Anonymous completions leave Completed false by design, so those tokens stay answerable.
        if (invitation.Completed) return null;

        var definition = await GetForEditAsync(invitation.SurveyId, ct);
        if (definition is null) return null;

        var draft = await repo.GetDraftResponseAsync(invitation.SurveyId, invitation.UserId, ct);
        var draftAnswers = draft is null
            ? (IReadOnlyList<SurveyDraftAnswer>)[]
            : draft.Answers
                .Select(a => new SurveyDraftAnswer(a.QuestionId, a.SelectedOptionValues, a.TextValue, a.RatingValue))
                .ToList();

        return new SurveyAnswerContext(
            invitation.SurveyId,
            invitation.Id,
            invitation.UserId,
            definition,
            draftAnswers,
            HasResumableDraft: draft is not null);
    }

    public async Task<Guid> StartIdentifiedDraftAsync(
        Guid surveyId, Guid invitationId, Guid userId, string culture, CancellationToken ct = default)
    {
        // Idempotent: one in-progress Identified draft per invitee.
        var existing = await repo.GetDraftResponseAsync(surveyId, userId, ct);
        if (existing is not null) return existing.Id;

        var response = new SurveyResponse
        {
            Id = Guid.NewGuid(),
            SurveyId = surveyId,
            InvitationId = invitationId,
            UserId = userId,
            Anonymity = ResponseAnonymity.Identified,
            InputMethod = SurveyInputMethod.UserSpecificLink,
            Culture = culture,
            SubmittedAt = null,
            Answers = [],
        };

        // No audit log for individual response activity (privacy — Deviation #10).
        await repo.AddResponseAsync(response, ct);
        return response.Id;
    }

    public Task MarkInvitationStartedAsync(Guid invitationId, CancellationToken ct = default)
        => repo.MarkInvitationStartedAsync(invitationId, ct);

    public async Task<SurveyPublicContext?> ResolvePublicContextAsync(string slug, CancellationToken ct = default)
    {
        var normalized = NormalizeSlug(slug);
        if (normalized is null) return null;

        var surveyId = await repo.GetIdByPublicSlugAsync(normalized, ct);
        if (surveyId is null) return null;

        var definition = await GetForEditAsync(surveyId.Value, ct);
        if (definition is null) return null;

        // A slug only answers when anonymous responding is allowed (e.g. AllowAnonymous was switched
        // off after the slug was set). The service guards this, not just the controller.
        if (!definition.Editable.AllowAnonymous) return null;

        return new SurveyPublicContext(surveyId.Value, definition);
    }

    public Task IncrementPublicStartedAsync(Guid surveyId, CancellationToken ct = default)
        => repo.IncrementPublicStartedAsync(surveyId, ct);

    public Task SaveDraftAnswersAsync(Guid draftResponseId, IReadOnlyList<SurveyAnswerInput> answers, CancellationToken ct = default)
        => repo.SaveDraftAnswersAsync(draftResponseId, MapAnswers(draftResponseId, answers), submittedAt: null, ct);

    public async Task SubmitResponseAsync(SurveySubmission submission, CancellationToken ct = default)
    {
        // Load the definition so branching can be re-evaluated authoritatively at submit time.
        var survey = await repo.GetByIdAsync(submission.SurveyId, ct)
            ?? throw new InvalidOperationException("Survey not found.");

        // The service is the authoritative gate: the controller's window checks are UX, this one is the rule.
        if (!SurveyWizardFlow.IsAnswerable(survey.Status, survey.OpensAt, survey.ClosesAt, clock.GetCurrentInstant()))
        {
            throw new InvalidOperationException("Survey is not open for responses.");
        }

        // Same authoritative posture for the duplicate gate: a completed invitation can't submit
        // again on the tracked tiers (Anonymous never flips Completed, so it is unaffected).
        if (submission.Anonymity != ResponseAnonymity.Anonymous && submission.InvitationId is { } gateInvId)
        {
            var invitation = await repo.GetInvitationByIdAsync(gateInvId, ct);
            if (invitation?.Completed == true)
            {
                throw new InvalidOperationException("This invitation has already submitted a response.");
            }
        }

        // Drop answers to questions hidden under full branching (defends against tampered/stale posts).
        var visibleAnswers = VisibleAnswers(survey, submission.Answers);

        switch (submission.Anonymity)
        {
            case ResponseAnonymity.Identified:
                {
                    var now = clock.GetCurrentInstant();
                    if (submission.DraftResponseId is { } draftId)
                    {
                        await repo.SaveDraftAnswersAsync(draftId, MapAnswers(draftId, visibleAnswers), submittedAt: now, ct);
                    }
                    else
                    {
                        var responseId = Guid.NewGuid();
                        var response = new SurveyResponse
                        {
                            Id = responseId,
                            SurveyId = submission.SurveyId,
                            InvitationId = submission.InvitationId,
                            UserId = submission.UserId,
                            Anonymity = ResponseAnonymity.Identified,
                            InputMethod = submission.InputMethod,
                            Culture = submission.Culture,
                            SubmittedAt = now,
                            Answers = MapAnswers(responseId, visibleAnswers),
                        };
                        await repo.AddResponseWithAnswersAndSaveAsync(response, ct);
                    }

                    if (submission.InvitationId is { } invId)
                    {
                        await repo.SetInvitationCompletedAsync(invId, ct);
                    }

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
                    await repo.AddResponseWithAnswersAndSaveAsync(response, ct);

                    if (submission.InvitationId is { } invId)
                    {
                        await repo.SetInvitationCompletedAsync(invId, ct);
                    }

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
    public async Task<SurveyWizardAdvanceResult> AdvanceWizardAsync(
        SurveyWizardState state, int page, bool back, IReadOnlyList<SurveyAnswerInput> postedAnswers,
        CancellationToken ct = default)
    {
        var definition = await GetForEditAsync(state.SurveyId, ct);
        if (definition is null) return new SurveyWizardAdvanceResult(SurveyWizardOutcome.NotFound, []);

        var editable = definition.Editable;
        if (!SurveyWizardFlow.IsAnswerable(definition.Status, editable.OpensAt, editable.ClosesAt, clock.GetCurrentInstant()))
        {
            return new SurveyWizardAdvanceResult(SurveyWizardOutcome.Closed, []);
        }

        // Only accept answers for questions actually visible on the posted page (re-evaluated server-side).
        var visibleBefore = SurveyWizardFlow.VisibleQuestionsOnPage(
            editable.Questions, page, SurveyWizardFlow.ToAnswerStates(state.Answers));
        var posted = postedAnswers.ToDictionary(a => a.QuestionId);

        foreach (var question in visibleBefore)
        {
            var id = question.Id!.Value;
            if (!posted.TryGetValue(id, out var answer))
            {
                state.Answers.Remove(id.ToString());
                continue;
            }

            state.Answers[id.ToString()] = new SurveyWizardAnswer
            {
                SelectedOptionValues = answer.SelectedOptionValues.Where(v => !string.IsNullOrEmpty(v)).ToList(),
                TextValue = string.IsNullOrWhiteSpace(answer.TextValue) ? null : answer.TextValue,
                RatingValue = answer.RatingValue,
            };
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

        // Identified per-page autosave (replace-all; the draft stays in-progress). Slug path is Anonymous — skipped.
        if (state.Anonymity == ResponseAnonymity.Identified && state.DraftResponseId is { } draftId)
        {
            await SaveDraftAnswersAsync(draftId, SurveyWizardFlow.ToAnswerInputs(state.Answers), ct);
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

        // No further visible page ⇒ submit. Identity columns are written only for Identified (see SubmitResponseAsync).
        await SubmitResponseAsync(new SurveySubmission(
            state.SurveyId,
            state.InvitationId,
            state.Anonymity == ResponseAnonymity.Identified ? state.UserId : null,
            state.DraftResponseId,
            state.Anonymity,
            state.InputMethod,
            state.Culture,
            SurveyWizardFlow.ToAnswerInputs(state.Answers)), ct);

        return new SurveyWizardAdvanceResult(SurveyWizardOutcome.Submitted, []);
    }

    public async Task<SurveyResultsView?> GetResultsAsync(Guid surveyId, CancellationToken ct = default)
    {
        var survey = await repo.GetByIdAsync(surveyId, ct);
        if (survey is null) return null;

        var culture = survey.DefaultCulture;
        var responses = await repo.GetResponsesForResultsAsync(surveyId, ct);
        var invited = await repo.GetInvitedCountsBySurveyAsync(ct);

        var invitedCount = invited.GetValueOrDefault(surveyId);
        var responseCount = responses.Count;
        var responseRate = invitedCount == 0 ? 0d : (double)responseCount / invitedCount;

        var questions = survey.Questions
            .OrderBy(q => q.PageNumber).ThenBy(q => q.Order)
            .Select(q => BuildQuestionAggregate(q, responses, culture))
            .ToList();

        var funnel = new SurveyFunnel(
            LinkStarted: await repo.GetStartedInvitationCountAsync(surveyId, ct),
            LinkFinished: responses.Count(r => r.InputMethod == SurveyInputMethod.UserSpecificLink),
            SlugStarted: survey.PublicStartedCount,
            SlugFinished: responses.Count(r => r.InputMethod == SurveyInputMethod.Slug));

        var identified = await BuildIdentifiedRespondentsAsync(survey, responses, culture, ct);

        return new SurveyResultsView(
            surveyId,
            survey.Title.Resolve(culture, culture),
            survey.Status,
            invitedCount,
            responseCount,
            responseRate,
            funnel,
            questions,
            identified);
    }

    public async Task<SurveyResponseExport?> GetResponseExportAsync(Guid surveyId, CancellationToken ct = default)
    {
        var survey = await repo.GetByIdAsync(surveyId, ct);
        if (survey is null) return null;

        var culture = survey.DefaultCulture;
        var responses = await repo.GetResponsesForResultsAsync(surveyId, ct);

        var orderedQuestions = survey.Questions
            .OrderBy(q => q.PageNumber).ThenBy(q => q.Order)
            .ToList();

        var questions = orderedQuestions
            .Select(q => new SurveyExportQuestion(
                q.Id,
                q.Prompt.Resolve(culture, culture),
                q.Type,
                q.Options.OrderBy(o => o.Order)
                    .Select(o => new SurveyExportOption(o.Value, o.Label.Resolve(culture, culture)))
                    .ToList()))
            .ToList();

        var optionLabels = survey.Questions.ToDictionary(
            q => q.Id,
            q => q.Options.ToDictionary(o => o.Value, o => o.Label.Resolve(culture, culture), StringComparer.Ordinal));

        // Identity is resolved only for Identified rows (no name lookup for tracked/anonymous responses).
        var identifiedUserIds = responses
            .Where(r => r.Anonymity == ResponseAnonymity.Identified && r.UserId.HasValue)
            .Select(r => r.UserId!.Value)
            .Distinct()
            .ToList();
        var users = identifiedUserIds.Count == 0
            ? (IReadOnlyDictionary<Guid, UserInfo>)new Dictionary<Guid, UserInfo>()
            : await userService.GetUserInfosAsync(identifiedUserIds, ct);

        var rows = responses
            .OrderBy(r => r.SubmittedAt)
            .Select(r =>
            {
                Guid? userId = null;
                string? userName = null;
                if (r.Anonymity == ResponseAnonymity.Identified && r.UserId is { } id)
                {
                    userId = id;
                    userName = users.TryGetValue(id, out var user) ? user.BurnerName : id.ToString();
                }

                var answers = r.Answers
                    .Select(a => new SurveyExportAnswer(
                        a.QuestionId,
                        a.SelectedOptionValues,
                        ResolveSelectedLabels(a, optionLabels),
                        a.TextValue,
                        a.RatingValue))
                    .ToList();

                return new SurveyExportRow(
                    r.Id, r.Anonymity, r.InputMethod, r.Culture, r.SubmittedAt, userId, userName, answers);
            })
            .ToList();

        return new SurveyResponseExport(surveyId, survey.Title.Resolve(culture, culture), culture, questions, rows);
    }

    /// <summary>
    /// GDPR Article 15 contributor: the user's own submitted <see cref="ResponseAnonymity.Identified"/>
    /// survey responses. CompletionTracked/Anonymous responses carry no <c>UserId</c> and are excluded by
    /// the repository query (not personal data linkable to the user). Prompts/labels are resolved in the
    /// response's own <see cref="SurveyResponse.Culture"/>, falling back to the survey's default culture.
    /// The collection slice is always emitted (an empty list, never null) so the export key stays stable.
    /// </summary>
    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var responses = await repo.GetIdentifiedResponsesForUserAsync(userId, ct);

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

                var prompts = survey is null
                    ? new Dictionary<Guid, string>()
                    : survey.Questions.ToDictionary(q => q.Id, q => q.Prompt.Resolve(culture, culture));
                var optionLabels = survey is null
                    ? new Dictionary<Guid, Dictionary<string, string>>()
                    : survey.Questions.ToDictionary(
                        q => q.Id,
                        q => q.Options.ToDictionary(o => o.Value, o => o.Label.Resolve(culture, culture), StringComparer.Ordinal));

                return new
                {
                    Survey = survey?.Title.Resolve(culture, culture) ?? r.SurveyId.ToString(),
                    SubmittedAt = r.SubmittedAt.ToIso8601(),
                    Culture = culture,
                    Answers = r.Answers.Select(a => new
                    {
                        Question = prompts.GetValueOrDefault(a.QuestionId, string.Empty),
                        SelectedLabels = ResolveSelectedLabels(a, optionLabels),
                        a.TextValue,
                        a.RatingValue,
                    }).ToList(),
                };
            })
            .ToList();

        return [new UserDataSlice(GdprExportSections.SurveyResponses, shaped)];
    }

    /// <summary>Aggregates one question across the submitted responses per its type (counts/distribution/free-text).</summary>
    private static QuestionAggregate BuildQuestionAggregate(
        SurveyQuestion question, IReadOnlyList<SurveyResponse> responses, string culture)
    {
        var prompt = question.Prompt.Resolve(culture, culture);
        var answers = responses
            .SelectMany(r => r.Answers)
            .Where(a => a.QuestionId == question.Id)
            .ToList();

        switch (question.Type)
        {
            case SurveyQuestionType.SingleChoice:
            case SurveyQuestionType.MultiChoice:
                {
                    // Percent base = respondents who answered THIS question, not all submissions —
                    // branched/optional questions aren't seen by everyone, so the total would skew low.
                    var answeredCount = answers.Count(a => a.SelectedOptionValues.Count > 0);
                    var optionCounts = question.Options
                        .OrderBy(o => o.Order)
                        .Select(o =>
                        {
                            var count = answers.Count(a => a.SelectedOptionValues.Contains(o.Value, StringComparer.Ordinal));
                            var percent = answeredCount == 0 ? 0d : (double)count / answeredCount * 100d;
                            return new OptionCount(o.Value, o.Label.Resolve(culture, culture), count, percent);
                        })
                        .ToList();
                    return new QuestionAggregate(question.Id, prompt, question.Type, optionCounts, [], null, []);
                }

            case SurveyQuestionType.Rating:
                {
                    var values = answers.Where(a => a.RatingValue.HasValue).Select(a => a.RatingValue!.Value).ToList();
                    var min = question.RatingMin ?? (values.Count > 0 ? values.Min() : 0);
                    var max = question.RatingMax ?? (values.Count > 0 ? values.Max() : min);
                    var distribution = new List<RatingBucket>();
                    for (var v = min; v <= max; v++)
                    {
                        distribution.Add(new RatingBucket(v, values.Count(rv => rv == v)));
                    }

                    double? average = values.Count > 0 ? values.Average() : null;
                    return new QuestionAggregate(question.Id, prompt, question.Type, [], distribution, average, []);
                }

            case SurveyQuestionType.ShortText:
            case SurveyQuestionType.LongText:
            default:
                {
                    var texts = answers
                        .Where(a => !string.IsNullOrEmpty(a.TextValue))
                        .Select(a => a.TextValue!)
                        .ToList();
                    return new QuestionAggregate(question.Id, prompt, question.Type, [], [], null, texts);
                }
        }
    }

    /// <summary>Builds the Identified-only drill-down, stitching display names via <c>IUserServiceRead</c>. Other tiers never appear (no identity exposure).</summary>
    private async Task<IReadOnlyList<RespondentDetail>> BuildIdentifiedRespondentsAsync(
        Survey survey, IReadOnlyList<SurveyResponse> responses, string culture, CancellationToken ct)
    {
        var identified = responses
            .Where(r => r.Anonymity == ResponseAnonymity.Identified && r.UserId.HasValue)
            .ToList();
        if (identified.Count == 0) return [];

        var userIds = identified.Select(r => r.UserId!.Value).Distinct().ToList();
        var users = await userService.GetUserInfosAsync(userIds, ct);

        var optionLabels = survey.Questions.ToDictionary(
            q => q.Id,
            q => q.Options.ToDictionary(o => o.Value, o => o.Label.Resolve(culture, culture), StringComparer.Ordinal));
        var prompts = survey.Questions.ToDictionary(q => q.Id, q => q.Prompt.Resolve(culture, culture));

        return identified
            .Select(r =>
            {
                var userId = r.UserId!.Value;
                var name = users.TryGetValue(userId, out var user) ? user.BurnerName : userId.ToString();
                var answers = r.Answers
                    .Select(a => new RespondentAnswer(
                        a.QuestionId,
                        prompts.GetValueOrDefault(a.QuestionId, string.Empty),
                        ResolveSelectedLabels(a, optionLabels),
                        a.TextValue,
                        a.RatingValue))
                    .ToList();
                return new RespondentDetail(userId, name, r.SubmittedAt, answers);
            })
            .ToList();
    }

    /// <summary>Maps an answer's selected option values to their resolved labels (falls back to the raw value when unknown).</summary>
    private static IReadOnlyList<string> ResolveSelectedLabels(
        SurveyAnswer answer, IReadOnlyDictionary<Guid, Dictionary<string, string>> optionLabels)
    {
        if (answer.SelectedOptionValues.Count == 0) return [];
        var labels = optionLabels.GetValueOrDefault(answer.QuestionId);
        return answer.SelectedOptionValues
            .Select(v => labels is not null && labels.TryGetValue(v, out var label) ? label : v)
            .ToList();
    }

    /// <summary>
    /// Keeps only the answers to questions visible under full cascading branching: an answer on a
    /// hidden question neither survives nor counts towards downstream <c>ShowIf</c> conditions.
    /// </summary>
    private static IReadOnlyList<SurveyAnswerInput> VisibleAnswers(Survey survey, IReadOnlyList<SurveyAnswerInput> answers)
    {
        var states = answers.ToDictionary(
            a => a.QuestionId,
            a => new AnswerState(a.SelectedOptionValues, a.TextValue, a.RatingValue));

        var effective = SurveyBranchingEvaluator.EffectiveAnswerStates(
            survey.Questions
                .OrderBy(q => q.PageNumber).ThenBy(q => q.Order)
                .Select(q => (q.Id, q.ShowIf)),
            states);

        return answers.Where(a => effective.ContainsKey(a.QuestionId)).ToList();
    }

    private static List<SurveyAnswer> MapAnswers(Guid responseId, IReadOnlyList<SurveyAnswerInput> answers)
        => answers.Select(a => new SurveyAnswer
        {
            Id = Guid.NewGuid(),
            ResponseId = responseId,
            QuestionId = a.QuestionId,
            SelectedOptionValues = a.SelectedOptionValues.ToList(),
            TextValue = a.TextValue,
            RatingValue = a.RatingValue,
        }).ToList();

    /// <summary>
    /// Resolves an audience predicate into the set of recipient user ids via cross-section read
    /// interfaces. No marketing opt-out filter — surveys are System/always-send.
    /// </summary>
    private async Task<IReadOnlySet<Guid>> ResolveRecipientIdsAsync(
        SurveyAudienceType type, Guid? teamId, CancellationToken ct)
    {
        switch (type)
        {
            case SurveyAudienceType.Team:
                {
                    if (teamId is null) return new HashSet<Guid>();
                    var team = await teamService.GetTeamAsync(teamId.Value, ct);
                    return team?.Members.Select(m => m.UserId).ToHashSet() ?? new HashSet<Guid>();
                }

            case SurveyAudienceType.AllActiveMembers:
                return (await ActiveMemberIdsAsync(ct)).ToHashSet();

            case SurveyAudienceType.TicketHolders:
                {
                    var orders = await ticketService.GetTicketOrdersAsync(ct);
                    return orders
                        .Where(o => o.IsCurrentEvent)
                        .SelectMany(o => o.Attendees)
                        .Where(a => a.MatchedUserId.HasValue
                            && (a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn))
                        .Select(a => a.MatchedUserId!.Value)
                        .ToHashSet();
                }

            case SurveyAudienceType.ShiftParticipants:
                {
                    var activeIds = await ActiveMemberIdsAsync(ct);
                    var views = await shiftView.GetUsersAsync(activeIds, ct);
                    return views
                        .Where(kv => kv.Value.HasShift)
                        .Select(kv => kv.Key)
                        .ToHashSet();
                }

            default:
                return new HashSet<Guid>();
        }
    }

    private async Task<List<Guid>> ActiveMemberIdsAsync(CancellationToken ct)
    {
        var users = await userService.GetAllUserInfosAsync(ct);
        return users
            .Where(u => u.IsApproved && !u.IsGdprAnonymized && !u.IsDeletionPending && !u.IsMerged)
            .Select(u => u.Id)
            .ToList();
    }

    /// <summary>Maps builder input to tracked entities, assigning new ids where the input id is null.</summary>
    private static List<SurveyQuestion> MapQuestions(Guid surveyId, SurveyEditInput input)
        => input.Questions.Select(q =>
        {
            var questionId = q.Id ?? Guid.NewGuid();
            return new SurveyQuestion
            {
                Id = questionId,
                SurveyId = surveyId,
                PageNumber = q.PageNumber,
                Order = q.Order,
                Type = q.Type,
                Prompt = q.Prompt,
                HelpText = q.HelpText,
                IsRequired = q.IsRequired,
                RatingMin = q.RatingMin,
                RatingMax = q.RatingMax,
                RatingMinLabel = q.RatingMinLabel,
                RatingMaxLabel = q.RatingMaxLabel,
                ShowIf = q.ShowIf,
                Options = q.Options.Select(o => new SurveyQuestionOption
                {
                    Id = o.Id ?? Guid.NewGuid(),
                    QuestionId = questionId,
                    Order = o.Order,
                    Value = o.Value,
                    Label = o.Label,
                }).ToList<SurveyQuestionOption>(),
            };
        }).ToList();

    private static void ValidateBranching(IReadOnlyList<SurveyQuestion> questions)
    {
        var offenders = SurveyBranchingEvaluator.ValidateNoForwardReferences(questions);
        if (offenders.Count > 0)
        {
            throw new InvalidOperationException(
                $"A branching condition references a question that is not strictly earlier. Offending question ids: {string.Join(", ", offenders)}.");
        }

        var emptyClauses = SurveyBranchingEvaluator.ValidateClauseOptionValues(questions);
        if (emptyClauses.Count > 0)
        {
            throw new InvalidOperationException(
                $"A branching Is/IsNot clause has no option values (the condition would be vacuous). Offending question ids: {string.Join(", ", emptyClauses)}.");
        }
    }

    /// <summary>
    /// Slugs that would shadow the literal-segment routes under <c>/Survey</c> (the answering wizard
    /// and the admin area). Authoring rejects these so a public link can never collide with a real action.
    /// </summary>
    private static readonly IReadOnlySet<string> ReservedSlugs =
        new HashSet<string>(StringComparer.Ordinal) { "admin", "answer" };

    /// <summary>Trims/lower-cases the slug (null when blank) and rejects reserved words.</summary>
    private static string? NormalizeSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return null;
        var normalized = slug.Trim().ToLowerInvariant();
        if (ReservedSlugs.Contains(normalized))
        {
            throw new InvalidOperationException($"Slug '{normalized}' is reserved.");
        }

        return normalized;
    }
}
