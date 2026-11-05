using Humans.GoogleIntegration.Contracts;
using Humans.Base.Extensions;
using Humans.Base.Threading;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Governance.Contracts;
using Humans.Users.Contracts;
using Humans.Surveys.Data;
using Humans.Shifts.Contracts;
using Humans.Surveys.Contracts;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;
using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.Surveys.Domain;
using NodaTime;

namespace Humans.Surveys.Services;

internal sealed class SurveyService(
    ISurveyRepository repo,
    IAuditLogService auditLog,
    IClock clock,
    ILogger<SurveyService> logger,
    ITeamService teamService,
    IUserService userService,
    IGovernanceService governanceService,
    ITicketService ticketService,
    IShiftView shiftView,
    IEmailService emailService,
    SurveysEmails emailMessages,
    ISurveyInviteTokenProvider tokenProvider,
    IGoogleTranslationService translation,
    IFileStorage fileStorage) : ISurveyService, IUserDataContributor, IUserMerge
{
    private readonly SurveyDefinitionReader definitionReader = new(repo, tokenProvider);
    private readonly SurveyResultsReader resultsReader = new(repo, userService, auditLog);
    private readonly SurveyDataExport dataExport = new(repo, userService);
    private readonly SurveyAudience audience = new(teamService, userService, governanceService, ticketService, shiftView, logger);
    private readonly SurveyImageUploads imageUploads = new(fileStorage, logger);
    private readonly SurveyTranslationFiller translationFiller = new(translation);

    private static readonly TrackedLock ResponseFinalizationLock = new("Surveys.ResponseFinalization");

    internal const string SurveyResponses = SurveyDataExport.SurveyResponses;
    internal const string AuthoredSurveys = SurveyDataExport.AuthoredSurveys;
    internal const string SurveyInvitations = SurveyDataExport.SurveyInvitations;

    // A shared epoch, not the real time: a CompletionTracked public start's real CreatedAt would
    // correlate with the unlinked response's SubmittedAt and unmask the respondent.
    private static readonly Instant NonCorrelatablePublicParticipationCreatedAt =
        Instant.FromUtc(1970, 1, 1, 0, 0);

    public Task<IReadOnlyList<SurveySummary>> GetSummariesAsync(CancellationToken ct = default) =>
        definitionReader.GetSummariesAsync(ct);

    public Task<SurveyOfficialLink?> GetOfficialLinkAsync(Guid surveyId, Guid userId, CancellationToken ct = default) =>
        definitionReader.GetOfficialLinkAsync(surveyId, userId, ct);

    public Task<SurveyDefinitionSnapshot?> GetDefinitionAsync(Guid surveyId, CancellationToken ct = default) =>
        definitionReader.GetDefinitionAsync(surveyId, ct);

    public Task<SurveyDetail?> GetForEditAsync(Guid surveyId, CancellationToken ct = default) =>
        definitionReader.GetForEditAsync(surveyId, ct);

    public Task<bool> HasSavedAnswersAsync(Guid surveyId, CancellationToken ct = default)
        => repo.HasSavedAnswersAsync(surveyId, ct);

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

    private async Task<SurveySaveResult> UpdateCoreAsync(
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
        var (input, filled) = await translationFiller.FillAsync(detail.Editable, targetCultures, ct);
        if (filled == 0) return 0;

        (await UpdateAsync(surveyId, input, viewer, ct)).RequireSuccess();
        logger.LogInformation(
            "Survey {SurveyId}: pre-filled {Count} missing translations from {Source}",
            surveyId, filled, detail.Editable.DefaultCulture);
        return filled;
    }

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
        var pending = (await repo.GetAllSummariesAsync(ct))
            .Where(s => s.Status == SurveyStatus.PendingApproval)
            .ToList();
        if (pending.Count == 0) return [];

        var authorIds = pending.Select(s => s.CreatedByUserId).Distinct().ToList();
        var authors = await userService.GetUserInfosAsync(authorIds, ct);

        return pending
            .Select(s => new SurveyPendingApprovalItem(
                s.Id,
                s.Title.Resolve(s.DefaultCulture, s.DefaultCulture),
                s.CreatedByUserId,
                authors.TryGetValue(s.CreatedByUserId, out var author) ? author.BurnerName : s.CreatedByUserId.ToString(),
                s.SubmittedAt))
            .ToList();
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
        var sent = await SendInvitesAsync(surveyId, viewer.UserId, CancellationToken.None);
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

    public async Task<int> PreviewAudienceCountAsync(Guid surveyId, CancellationToken ct = default)
    {
        var survey = await repo.GetByIdAsync(surveyId, ct);
        if (survey?.AudienceType is null) return 0;
        var audienceType = survey.AudienceType.Value;
        if (Enum.IsDefined(audienceType) &&
            SurveyDefinitionValidation.AudienceConfigurationError(
                audienceType, survey.AudienceTeamId, survey.AudienceLoggedInSince, requireAudience: true) is not null)
            return 0;

        var recipients = await audience.ResolveRecipientIdsAsync(
            audienceType, survey.AudienceTeamId, survey.AudienceLoggedInSince, ct);
        if (recipients.Count == 0) return 0;
        var participationRows = await repo.GetInvitationsAsync(surveyId, ct);
        var alreadyInvited = await InvitedUserIdsResolvedAsync(participationRows, ct);
        var completedPublicParticipants = participationRows
            .Where(invitation => invitation.SentAt is null && invitation.Completed)
            .Select(invitation => invitation.UserId);
        return recipients.Except(alreadyInvited).Except(completedPublicParticipants).Count();
    }

    public async Task<SendResult> SendInvitesAsync(Guid surveyId, Guid actorUserId, CancellationToken ct = default)
    {
        var survey = await repo.GetByIdAsync(surveyId, ct);
        if (survey is null)
            return new SendResult(0, 0, 0, "Survey not found.");
        if (survey.Status != SurveyStatus.Open)
            return new SendResult(0, 0, 0, "Invitations can only be sent for an Open survey.");
        var audienceError = SurveyDefinitionValidation.AudienceConfigurationError(
            survey.AudienceType, survey.AudienceTeamId, survey.AudienceLoggedInSince, requireAudience: true);
        if (audienceError is not null)
            return new SendResult(0, 0, 0, audienceError.DiagnosticMessage);
        var audienceType = survey.AudienceType!.Value;

        var now = clock.GetCurrentInstant();
        var target = await audience.ResolveRecipientIdsAsync(
            audienceType, survey.AudienceTeamId, survey.AudienceLoggedInSince, ct);
        var participationRows = await repo.GetInvitationsAsync(surveyId, ct);
        var alreadyInvited = await InvitedUserIdsResolvedAsync(participationRows, ct);
        var completedPublicParticipants = participationRows
            .Where(invitation => invitation.SentAt is null && invitation.Completed)
            .Select(invitation => invitation.UserId);
        var netNew = target
            .Except(alreadyInvited)
            .Except(completedPublicParticipants)
            .ToList();
        var existingParticipation = participationRows.ToDictionary(invitation => invitation.UserId);

        var users = await userService.GetUserInfosAsync(netNew, ct);
        var invitationsCreated = 0;
        var emailsQueued = 0;
        var failed = 0;

        // Once invitation writes begin, finish the batch independently of request cancellation.
        // Stamp SentAt only after enqueue acceptance so failures stay eligible for retry.
        ct.ThrowIfCancellationRequested();
        var sendCt = CancellationToken.None;

        foreach (var userId in netNew)
        {
            if (!users.TryGetValue(userId, out var user) || user.Email is not { } email || string.IsNullOrWhiteSpace(email))
            {
                logger.LogWarning(
                    "User {UserId} has no notification email for survey {SurveyId}; skipping invitation",
                    userId, surveyId);
                continue;
            }

            SurveyInvitation inv;
            if (existingParticipation.TryGetValue(userId, out var existing))
            {
                inv = existing;
            }
            else
            {
                inv = new SurveyInvitation
                {
                    Id = Guid.NewGuid(),
                    SurveyId = surveyId,
                    UserId = userId,
                    CreatedAt = now,
                };
                await repo.AddInvitationAndSaveAsync(inv, sendCt);
            }
            invitationsCreated++;

            try
            {
                var preferredCulture = user.PreferredLanguage;
                var culture = preferredCulture.IsSupportedCultureCode()
                    ? preferredCulture!
                    : survey.DefaultCulture;
                var name = user.BurnerName;
                var title = survey.Title.Resolve(culture, survey.DefaultCulture);
                var customSubject = survey.InvitationEmailSubject.ResolveOptional(culture, survey.DefaultCulture);
                var customMessage = survey.InvitationEmailMessage.ResolveOptional(culture, survey.DefaultCulture);
                var token = tokenProvider.Create(inv.Id);
                var msg = emailMessages.SurveyInvitation(
                    userId,
                    email, name, title, token, culture, customSubject, customMessage);

                await emailService.SendAsync(msg, sendCt);
                await repo.UpdateInvitationStatusAsync(
                    inv.Id, EmailOutboxStatus.Queued, clock.GetCurrentInstant(), sendCt);
                emailsQueued++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to prepare, enqueue or record survey invitation email for user {UserId} invitation {InvitationId} in survey {SurveyId}",
                    userId, inv.Id, surveyId);
                await repo.UpdateInvitationStatusAsync(inv.Id, EmailOutboxStatus.Failed, now, sendCt);
                failed++;
            }
        }

        await auditLog.LogAsync(SurveysAuditActions.SurveyInvitesSent, AuditEntityTypes.Survey, surveyId,
            $"Sent {invitationsCreated} invitation(s)", actorUserId);

        return new SendResult(invitationsCreated, emailsQueued, failed);
    }

    /// <summary>
    /// Invited ids plus, for any that has since been merged away, its survivor. Invitations are
    /// never re-pointed on merge, so the audience (live ids) is compared against both, or the
    /// survivor is invited a second time to a survey their archived id already holds.
    /// </summary>
    private async Task<HashSet<Guid>> InvitedUserIdsResolvedAsync(
        IReadOnlyList<SurveyInvitation> invitations, CancellationToken ct)
    {
        var invited = invitations.Where(invitation => invitation.SentAt is not null)
            .Select(invitation => invitation.UserId).ToHashSet();
        if (invited.Count == 0) return invited;
        foreach (var info in (await userService.GetUserInfosAsync(invited.ToList(), ct)).Values)
            invited.Add(info.Id);
        return invited;
    }

    public async Task<IReadOnlyList<SurveyInviteStatus>> GetInviteStatusesAsync(Guid surveyId, CancellationToken ct = default)
    {
        var invitations = (await repo.GetInvitationsAsync(surveyId, ct))
            .Where(invitation => invitation.SentAt is not null)
            .ToList();
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

    public Task<bool> IsEligibleAsociadoAsync(Guid userId, CancellationToken ct = default) =>
        audience.IsEligibleAsociadoAsync(userId, ct);

    public async Task<Guid> StartIdentifiedDraftAsync(
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

    public async Task<SurveyPublicStart?> StartPublicTrackedResponseAsync(
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

    public Task MarkInvitationStartedAsync(Guid invitationId, CancellationToken ct = default)
        => repo.MarkInvitationStartedAsync(invitationId, ct);

    public async Task<SurveyPublicContext?> ResolvePublicContextAsync(
        string slug,
        Guid? userId,
        CancellationToken ct = default)
    {
        // Invalid public URLs are lookup misses; authoring rejects invalid slugs instead.
        var (normalized, slugError) = SurveyDefinitionValidation.PreparePublicSlugForWrite(slug);
        if (normalized is null || slugError is not null) return null;

        var surveyId = await repo.GetIdByPublicSlugAsync(normalized, ct);
        if (surveyId is null) return null;

        var definition = await GetForEditAsync(surveyId.Value, ct);
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

    public Task IncrementPublicStartedAsync(Guid surveyId, CancellationToken ct = default)
        => repo.IncrementPublicStartedAsync(surveyId, ct);

    public async Task SaveDraftAnswersAsync(
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
            if (invitation?.Completed == true)
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

    public Task<SurveyResultsView?> GetResultsAsync(Guid surveyId, CancellationToken ct = default) =>
        resultsReader.GetResultsAsync(surveyId, ct);

    public Task<SurveyScopedResults?> GetScopedResultsAsync(
        Guid surveyId, SurveyResultsScope scope, CancellationToken ct = default) =>
        resultsReader.GetScopedResultsAsync(surveyId, scope, ct);

    public Task<SurveyResponseExport?> GetResponseExportAsync(
        Guid surveyId, Guid actorUserId, CancellationToken ct = default) =>
        resultsReader.GetResponseExportAsync(surveyId, actorUserId, ct);

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
    public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        dataExport.ContributeForUserAsync(userId, ct);

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

    /// <summary>Builds the Identified-only drill-down; views render identities by user ID. Other tiers never appear (no identity exposure).</summary>
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
