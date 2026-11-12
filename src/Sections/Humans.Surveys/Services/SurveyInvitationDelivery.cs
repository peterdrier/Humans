using System.Security.Cryptography;
using System.Text;
using Humans.AuditLog.Contracts;
using Humans.Base.Enums;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Surveys.Contracts;
using Humans.Surveys.Data;
using Humans.Surveys.Domain;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Surveys.Services;

/// <summary>Owns invitation audience previews, delivery status and initial/reminder email batches.</summary>
internal sealed class SurveyInvitationDelivery(
    ISurveyRepository repo,
    IAuditLogService auditLog,
    IClock clock,
    IUserService userService,
    IEmailService emailService,
    SurveysEmails emailMessages,
    ISurveyInviteTokenProvider tokenProvider,
    SurveyAudience audience,
    ILogger<SurveyInvitationDelivery> logger) : ISurveyReminderSender, IApplicationService
{
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
        var excluded = await ExcludedRecipientUserIdsAsync(participationRows, ct);
        return recipients.Except(excluded).Count();
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
        var excluded = await ExcludedRecipientUserIdsAsync(participationRows, ct);
        var netNew = target.Except(excluded).ToList();
        var existingParticipation = participationRows.ToDictionary(invitation => invitation.UserId);

        var users = await userService.GetUserInfosAsync(netNew, ct);
        var invitationsProcessed = 0;
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
            invitationsProcessed++;

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
                // Retry the invitation delivery without consuming the reminder's invitation-id delivery key.
                var deliveryKey = $"{msg.TemplateName}:{inv.Id:N}";
                msg = msg with
                {
                    DeliveryId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(deliveryKey)).AsSpan(0, 16))
                };

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
            $"Processed {invitationsProcessed} invitation(s); {emailsQueued} email(s) queued, {failed} failed.", actorUserId);

        return new SendResult(invitationsProcessed, emailsQueued, failed);
    }

    /// <summary>
    /// Emailed invitees and completed public participants, including their merge survivors. Participation
    /// rows keep their original ids, but the live audience must not re-invite the same human under a new id.
    /// </summary>
    private async Task<HashSet<Guid>> ExcludedRecipientUserIdsAsync(
        IReadOnlyList<SurveyInvitation> invitations, CancellationToken ct)
    {
        var excluded = invitations.Where(invitation => invitation.SentAt is not null || invitation.Completed)
            .Select(invitation => invitation.UserId).ToHashSet();
        if (excluded.Count == 0) return excluded;
        foreach (var info in (await userService.GetUserInfosAsync(excluded.ToList(), ct)).Values)
            excluded.Add(info.Id);
        return excluded;
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

    public async Task<int> SendDueRemindersAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var cutoff = now - Duration.FromDays(7);

        var due = await repo.GetInvitationsDueForReminderAsync(cutoff, ct);
        if (due.Count == 0) return 0;

        var userIds = due.Select(i => i.UserId).Distinct().ToList();
        var users = await userService.GetUserInfosAsync(userIds, ct);

        // Loaded once per distinct survey (few open surveys at this scale). The answer window is
        // re-checked here rather than in the query: Open alone is not answerable, and reminding
        // someone about a survey past its ClosesAt sends them to the Closed page — and spends their
        // one ReminderSentAt stamp doing it.
        var surveys = new Dictionary<Guid, (LocalizedText Title, string DefaultCulture, bool Answerable)>();

        var reminded = 0;
        var failures = new List<Exception>();
        foreach (var inv in due)
        {
            // An invitation held by a since-merged id belongs to the survivor's past: the
            // survivor's own invitation (if any) carries the reminder, and for an Asociado vote
            // this token could no longer answer. Left unstamped and skipped, like a missing email.
            if (users.TryGetValue(inv.UserId, out var holder) && holder.Id != inv.UserId)
            {
                logger.LogInformation(
                    "Invitation {InvitationId} belongs to merged user {UserId}; skipping reminder",
                    inv.Id, inv.UserId);
                continue;
            }

            if (!users.TryGetValue(inv.UserId, out var user) || user.Email is not { } email || string.IsNullOrWhiteSpace(email))
            {
                logger.LogWarning(
                    "User {UserId} has no notification email for survey {SurveyId}; skipping reminder",
                    inv.UserId, inv.SurveyId);
                continue;
            }

            // Keep metadata loading inside the per-invitee guard too: a failed read
            // must leave this invitation due without blocking later reminders.
            try
            {
                if (!surveys.TryGetValue(inv.SurveyId, out var meta))
                {
                    var survey = await repo.GetByIdAsync(inv.SurveyId, ct);
                    if (survey is null) continue;
                    meta = (
                        survey.Title,
                        survey.DefaultCulture,
                        SurveyWizardFlow.IsAnswerable(survey.Status, survey.OpensAt, survey.ClosesAt, now));
                    surveys[inv.SurveyId] = meta;
                }

                if (!meta.Answerable) continue;

                var preferredCulture = user.PreferredLanguage;
                var culture = preferredCulture.IsSupportedCultureCode()
                    ? preferredCulture!
                    : meta.DefaultCulture;
                var name = user.BurnerName;
                var token = tokenProvider.Create(inv.Id);
                var title = meta.Title.Resolve(culture, meta.DefaultCulture);
                var msg = emailMessages.SurveyReminder(inv.UserId, email, name, title, token, culture)
                    with
                { DeliveryId = inv.Id };

                await emailService.SendAsync(msg, ct);
                await repo.SetReminderSentAsync(inv.Id, now, ct);
                reminded++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(ex);
                logger.LogError(ex,
                    "Failed to enqueue survey reminder email for user {UserId} invitation {InvitationId} in survey {SurveyId}",
                    inv.UserId, inv.Id, inv.SurveyId);
            }
        }

        var summary = $"Sent {reminded} survey reminder(s)";
        if (failures.Count > 0) summary += $"; {failures.Count} failed";
        await auditLog.LogAsync(SurveysAuditActions.SurveyReminderSent, AuditEntityTypes.Survey, Guid.Empty,
            summary, jobName: AuditEntityTypes.ReminderJob);

        if (failures.Count > 0)
            throw new AggregateException("Failed to complete one or more survey reminders.", failures);
        return reminded;
    }
}
