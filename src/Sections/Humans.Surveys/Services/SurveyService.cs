using Humans.GoogleIntegration.Contracts;
using Humans.Base.Extensions;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
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

/// <summary>
/// Application-layer <see cref="ISurveyService"/>. Plain Scoped service (no caching decorator, per
/// spec §12). Cross-domain display data is stitched from <c>I…ServiceRead</c> interfaces — the
/// repository never resolves user/team navs.
/// </summary>
internal sealed class SurveyService(
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
    IGoogleTranslationService translation,
    IFileStorage fileStorage) : ISurveyService, ISurveyReminderSender, IUserDataContributor
{
    private const int InvitationEmailSubjectMaxLength = 200;
    private const int InvitationEmailMessageMaxLength = 4000;
    private const int MaxInformationImages = 5;
    private const long MaxInformationImageBytes = 10 * 1024 * 1024;
    private static readonly Instant NonCorrelatablePublicParticipationCreatedAt =
        Instant.FromUtc(1970, 1, 1, 0, 0);
    private static readonly HashSet<string> AllowedInformationImageContentTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/webp" };
    private static readonly HashSet<string> AllowedInformationImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };

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

    /// <summary>
    /// The machine-readable question graph behind <c>/api/backdoor/surveys/{id}</c>. Built on
    /// <see cref="GetForEditAsync"/> so there is one read path, then flattened: localized text
    /// resolved once in the survey's default culture, and the stored branching payload mirrored
    /// onto <see cref="SurveyBranchCondition"/> so the persisted jsonb shape stays private.
    /// </summary>
    public async Task<SurveyDefinitionSnapshot?> GetDefinitionAsync(Guid surveyId, CancellationToken ct = default)
    {
        var detail = await GetForEditAsync(surveyId, ct);
        if (detail is null) return null;

        var e = detail.Editable;
        var culture = e.DefaultCulture;

        return new SurveyDefinitionSnapshot(
            detail.Id,
            e.Title.Resolve(culture, culture),
            detail.Status,
            culture,
            [.. e.Questions
                .OrderBy(q => q.PageNumber)
                .ThenBy(q => q.Order)
                .Select(q => new SurveyDefinitionQuestion(
                    // Persisted questions always carry an id; the nullable is the authoring
                    // shape, where a not-yet-saved question has none.
                    q.Id ?? Guid.Empty,
                    q.PageNumber,
                    q.Order,
                    q.Type,
                    q.Prompt.Resolve(culture, culture),
                    q.Type == SurveyQuestionType.Information ? q.HelpText.Resolve(culture, culture) : null,
                    q.IsRequired,
                    q.RatingMin,
                    q.RatingMax,
                    ToBranchCondition(q.ShowIf),
                    q.GridSelectionMode,
                    [.. (q.GridRows ?? [])
                        .Select(row => new SurveyExportGridRow(row.Value, row.Label.Resolve(culture, culture)))],
                    [.. (q.InformationImages ?? [])
                        .Where(image => !string.IsNullOrWhiteSpace(image.StoragePath))
                        .Select(image => new SurveyDefinitionImage(
                            image.Id ?? Guid.Empty,
                            $"/{image.StoragePath!.TrimStart('/')}",
                            image.Label.Resolve(culture, culture),
                            image.AltText.Resolve(culture, culture)))],
                     [.. q.Options
                         .OrderBy(o => o.Order)
                         .Select(o => new SurveyExportOption(o.Value, o.Label.Resolve(culture, culture)))],
                     ToRankedSettings(q.RankedSettings),
                     q.RankedUnavailableOptionValues?.ToList()))]);
    }

    private static SurveyBranchCondition? ToBranchCondition(BranchCondition? condition) =>
        condition is null
            ? null
            : new SurveyBranchCondition(
                condition.Combine.ToString(),
                [.. condition.Clauses.Select(c => new SurveyBranchClause(
                    c.QuestionId, c.Operator.ToString(), c.OptionValues))]);

    public async Task<SurveyDetail?> GetForEditAsync(Guid surveyId, CancellationToken ct = default)
    {
        var s = await repo.GetByIdAsync(surveyId, ct);
        if (s is null) return null;

        var input = new SurveyEditInput(
            s.Title, s.Intro, s.ThankYou, s.InvitationEmailSubject, s.InvitationEmailMessage,
            s.DefaultCulture, s.AllowAnonymous, s.OpensAt, s.ClosesAt,
            s.AudienceType, s.AudienceTeamId, s.AudienceLoggedInSince, s.PublicSlug,
            ToQuestionInputs(s),
            s.IsAsociadoVote == true);

        return new SurveyDetail(s.Id, s.Status, input);
    }

    public Task<bool> HasSavedAnswersAsync(Guid surveyId, CancellationToken ct = default)
        => repo.HasSavedAnswersAsync(surveyId, ct);

    public async Task<Guid> CreateAsync(SurveyEditInput input, Guid actorUserId, CancellationToken ct = default)
    {
        ValidateAudienceConfiguration(
            input.AudienceType, input.AudienceTeamId, input.AudienceLoggedInSince, requireAudience: false);
        var invitationEmailSubject = NormalizeLocalizedText(input.InvitationEmailSubject);
        var invitationEmailMessage = NormalizeLocalizedText(input.InvitationEmailMessage);
        ValidateInvitationEmailCopy(invitationEmailSubject, invitationEmailMessage);
        var now = clock.GetCurrentInstant();
        var surveyId = Guid.NewGuid();
        var prepared = await PrepareInformationImagesAsync(surveyId, input, existing: null, ct);
        List<SurveyQuestion> questions;
        try
        {
            questions = MapQuestions(surveyId, prepared.Input);
            ValidateQuestionConfiguration(questions);
            ValidateBranching(questions);
            ValidateVoteConfiguration(input);
        }
        catch
        {
            await DeleteFilesBestEffortAsync(prepared.NewStoragePaths, CancellationToken.None);
            throw;
        }

        var survey = new Survey
        {
            Id = surveyId,
            Title = input.Title,
            Intro = input.Intro,
            ThankYou = input.ThankYou,
            InvitationEmailSubject = invitationEmailSubject,
            InvitationEmailMessage = invitationEmailMessage,
            DefaultCulture = input.DefaultCulture,
            AllowAnonymous = input.AllowAnonymous,
            IsAsociadoVote = input.IsAsociadoVote,
            Status = SurveyStatus.Draft,
            OpensAt = input.OpensAt,
            ClosesAt = input.ClosesAt,
            AudienceType = input.AudienceType,
            AudienceTeamId = input.AudienceTeamId,
            AudienceLoggedInSince = input.AudienceLoggedInSince,
            PublicSlug = NormalizeSlug(input.PublicSlug),
            CreatedByUserId = actorUserId,
            CreatedAt = now,
            UpdatedAt = now,
            Questions = questions,
        };

        try
        {
            await repo.AddAsync(survey, ct);
        }
        catch
        {
            await DeleteFilesBestEffortAsync(prepared.NewStoragePaths, CancellationToken.None);
            throw;
        }
        logger.LogInformation("Survey {SurveyId} created by {UserId}", surveyId, actorUserId);
        await auditLog.LogAsync(AuditAction.SurveyCreated, AuditEntityTypes.Survey, surveyId,
            $"Created survey '{survey.Title.Resolve(survey.DefaultCulture, survey.DefaultCulture)}'", actorUserId);
        return surveyId;
    }

    public Task UpdateAsync(Guid surveyId, SurveyEditInput input, Guid actorUserId, CancellationToken ct = default)
        => UpdateCoreAsync(surveyId, input, actorUserId, allowRankedAvailabilityChanges: false, ct);

    private async Task UpdateCoreAsync(
        Guid surveyId,
        SurveyEditInput input,
        Guid actorUserId,
        bool allowRankedAvailabilityChanges,
        CancellationToken ct)
    {
        ValidateAudienceConfiguration(
            input.AudienceType, input.AudienceTeamId, input.AudienceLoggedInSince, requireAudience: false);
        var invitationEmailSubject = NormalizeLocalizedText(input.InvitationEmailSubject);
        var invitationEmailMessage = NormalizeLocalizedText(input.InvitationEmailMessage);
        ValidateInvitationEmailCopy(invitationEmailSubject, invitationEmailMessage);
        var now = clock.GetCurrentInstant();
        var existing = await repo.GetByIdAsync(surveyId, ct)
            ?? throw new InvalidOperationException("Survey not found.");
        if (existing.IsAsociadoVote == true
            && existing.Status != SurveyStatus.Draft
            && !allowRankedAvailabilityChanges)
        {
            throw new InvalidOperationException(
                "An Asociado vote cannot be edited after it has opened.");
        }
        if (existing.Status != SurveyStatus.Draft
            && (existing.IsAsociadoVote == true) != input.IsAsociadoVote)
        {
            throw new InvalidOperationException(
                "Asociado vote mode cannot change after the survey has opened.");
        }
        var prepared = await PrepareInformationImagesAsync(surveyId, input, existing, ct);
        List<SurveyQuestion> questions;
        try
        {
            questions = MapQuestions(surveyId, prepared.Input);
            if (!allowRankedAvailabilityChanges)
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
            ValidateQuestionConfiguration(questions);
            ValidateBranching(questions);
            ValidateVoteConfiguration(input);
            if (await repo.HasSavedAnswersAsync(surveyId, ct))
            {
                ValidateRankedDefinitionFrozen(existing.Questions, questions);
            }
        }
        catch
        {
            await DeleteFilesBestEffortAsync(prepared.NewStoragePaths, CancellationToken.None);
            throw;
        }

        var survey = new Survey
        {
            Id = surveyId,
            Title = input.Title,
            Intro = input.Intro,
            ThankYou = input.ThankYou,
            InvitationEmailSubject = invitationEmailSubject,
            InvitationEmailMessage = invitationEmailMessage,
            DefaultCulture = input.DefaultCulture,
            AllowAnonymous = input.AllowAnonymous,
            IsAsociadoVote = input.IsAsociadoVote,
            OpensAt = input.OpensAt,
            ClosesAt = input.ClosesAt,
            AudienceType = input.AudienceType,
            AudienceTeamId = input.AudienceTeamId,
            AudienceLoggedInSince = input.AudienceLoggedInSince,
            PublicSlug = NormalizeSlug(input.PublicSlug),
            UpdatedAt = now,
            Questions = questions,
        };

        // Diffed against the no-tracking snapshot so the audit trail names what changed.
        var changeSummary = DescribeSurveyChanges(existing, survey);
        try
        {
            await repo.UpdateAsync(survey, ct);
        }
        catch
        {
            await DeleteFilesBestEffortAsync(prepared.NewStoragePaths, CancellationToken.None);
            throw;
        }
        await auditLog.LogAsync(AuditAction.SurveyUpdated, AuditEntityTypes.Survey, surveyId, changeSummary, actorUserId);
    }

    /// <summary>
    /// Short field list for the SurveyUpdated audit entry — names only, no before/after values,
    /// except the governance-relevant audience type and slug transitions. Question edits are
    /// collapsed to counts; deep content (grid rows, images, branching) is not diffed, so an
    /// update touching only those falls back to the bare "Updated survey".
    /// </summary>
    private static string DescribeSurveyChanges(Survey existing, Survey updated)
    {
        var changes = new List<string>();
        if (!existing.Title.Equals(updated.Title)) changes.Add("title");
        if (!existing.Intro.Equals(updated.Intro)) changes.Add("intro");
        if (!existing.ThankYou.Equals(updated.ThankYou)) changes.Add("thank-you text");
        if (!existing.InvitationEmailSubject.Equals(updated.InvitationEmailSubject)) changes.Add("invitation subject");
        if (!existing.InvitationEmailMessage.Equals(updated.InvitationEmailMessage)) changes.Add("invitation message");
        if (!string.Equals(existing.DefaultCulture, updated.DefaultCulture, StringComparison.OrdinalIgnoreCase))
            changes.Add($"default culture ({existing.DefaultCulture} → {updated.DefaultCulture})");
        if (existing.AllowAnonymous != updated.AllowAnonymous)
            changes.Add(updated.AllowAnonymous ? "anonymous responses enabled" : "anonymous responses disabled");
        if ((existing.IsAsociadoVote == true) != (updated.IsAsociadoVote == true))
            changes.Add(updated.IsAsociadoVote == true ? "Asociado vote enabled" : "Asociado vote disabled");
        if (existing.OpensAt != updated.OpensAt) changes.Add("opens-at");
        if (existing.ClosesAt != updated.ClosesAt) changes.Add("closes-at");
        if (existing.AudienceType != updated.AudienceType)
            changes.Add($"audience ({existing.AudienceType?.ToString() ?? "none"} → {updated.AudienceType?.ToString() ?? "none"})");
        else if (existing.AudienceTeamId != updated.AudienceTeamId || existing.AudienceLoggedInSince != updated.AudienceLoggedInSince)
            changes.Add("audience");
        if (!string.Equals(existing.PublicSlug, updated.PublicSlug, StringComparison.Ordinal))
            changes.Add(existing.PublicSlug is null ? "public slug set"
                : updated.PublicSlug is null ? "public slug removed"
                : "public slug changed");

        var oldQuestions = existing.Questions.ToDictionary(q => q.Id);
        var newQuestions = updated.Questions.ToDictionary(q => q.Id);
        foreach (var question in newQuestions.Values.Where(q => q.Type == SurveyQuestionType.RankedChoice))
        {
            if (!oldQuestions.TryGetValue(question.Id, out var oldQuestion)) continue;
            var oldUnavailable = (oldQuestion.RankedUnavailableOptionValues ?? [])
                .ToHashSet(StringComparer.Ordinal);
            var newUnavailable = (question.RankedUnavailableOptionValues ?? [])
                .ToHashSet(StringComparer.Ordinal);
            var addedUnavailable = newUnavailable.Except(oldUnavailable, StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            var restored = oldUnavailable.Except(newUnavailable, StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            if (addedUnavailable.Count > 0)
                changes.Add($"ranked options unavailable ({string.Join(", ", addedUnavailable)})");
            if (restored.Count > 0)
                changes.Add($"ranked options restored ({string.Join(", ", restored)})");
        }

        var added = newQuestions.Keys.Count(id => !oldQuestions.ContainsKey(id));
        var removed = oldQuestions.Keys.Count(id => !newQuestions.ContainsKey(id));
        var edited = newQuestions.Values.Count(q =>
            oldQuestions.TryGetValue(q.Id, out var old) && QuestionChanged(old, q));
        if (added > 0) changes.Add($"{added} question(s) added");
        if (removed > 0) changes.Add($"{removed} question(s) removed");
        if (edited > 0) changes.Add($"{edited} question(s) edited");

        return changes.Count == 0 ? "Updated survey" : $"Updated survey: {string.Join(", ", changes)}";
    }

    private static bool QuestionChanged(SurveyQuestion old, SurveyQuestion updated) =>
        old.Type != updated.Type
        || old.PageNumber != updated.PageNumber
        || old.Order != updated.Order
        || old.IsRequired != updated.IsRequired
        || !old.Prompt.Equals(updated.Prompt)
        || !old.HelpText.Equals(updated.HelpText)
        || old.RatingMin != updated.RatingMin
        || old.RatingMax != updated.RatingMax
        || !old.RatingMinLabel.Equals(updated.RatingMinLabel)
        || !old.RatingMaxLabel.Equals(updated.RatingMaxLabel)
        || old.GridSelectionMode != updated.GridSelectionMode
        || old.RankedSettings != updated.RankedSettings
        || !(old.RankedUnavailableOptionValues ?? [])
            .SequenceEqual(updated.RankedUnavailableOptionValues ?? [], StringComparer.Ordinal)
        || !OptionsEqual(old.Options, updated.Options);

    private static bool OptionsEqual(ICollection<SurveyQuestionOption> old, ICollection<SurveyQuestionOption> updated) =>
        old.Count == updated.Count
        && old.OrderBy(o => o.Order).Zip(updated.OrderBy(o => o.Order))
            .All(pair => pair.First.Order == pair.Second.Order
                && string.Equals(pair.First.Value, pair.Second.Value, StringComparison.Ordinal)
                && pair.First.Label.Equals(pair.Second.Label));

    public async Task<int> PreFillTranslationsAsync(
        Guid surveyId, IReadOnlyList<string> targetCultures, Guid actorUserId, CancellationToken ct = default)
    {
        var detail = await GetForEditAsync(surveyId, ct)
            ?? throw new InvalidOperationException("Survey not found.");
        if (detail.Editable.IsAsociadoVote && detail.Status != SurveyStatus.Draft)
        {
            throw new InvalidOperationException(
                "An Asociado vote cannot be edited after it has opened.");
        }
        var e = detail.Editable;
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

        if (filled == 0) return 0;

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
        if (status == SurveyStatus.Closed)
        {
            var survey = await repo.GetByIdAsync(surveyId, ct)
                ?? throw new InvalidOperationException("Survey not found.");
            if (survey.IsAsociadoVote == true)
                throw new InvalidOperationException("A closed Asociado vote cannot be reopened.");
        }

        await repo.SetStatusAsync(surveyId, SurveyStatus.Open, clock.GetCurrentInstant(), ct);
        await auditLog.LogAsync(AuditAction.SurveyOpened, AuditEntityTypes.Survey, surveyId, "Opened survey", actorUserId);
    }

    public async Task CloseAsync(Guid surveyId, Guid actorUserId, CancellationToken ct = default)
    {
        var status = await repo.GetStatusAsync(surveyId, ct)
            ?? throw new InvalidOperationException("Survey not found.");
        if (status == SurveyStatus.Closed) return;

        await repo.SetStatusAsync(surveyId, SurveyStatus.Closed, clock.GetCurrentInstant(), ct);
        await auditLog.LogAsync(AuditAction.SurveyClosed, AuditEntityTypes.Survey, surveyId, "Closed survey", actorUserId);
    }

    public async Task<int> PreviewAudienceCountAsync(Guid surveyId, CancellationToken ct = default)
    {
        var survey = await repo.GetByIdAsync(surveyId, ct);
        if (survey?.AudienceType is null) return 0;
        var audienceType = survey.AudienceType.Value;
        if (Enum.IsDefined(audienceType) &&
            AudienceConfigurationError(
                audienceType, survey.AudienceTeamId, survey.AudienceLoggedInSince, requireAudience: true) is not null)
            return 0;

        var recipients = await ResolveRecipientIdsAsync(
            audienceType, survey.AudienceTeamId, survey.AudienceLoggedInSince, ct);
        if (recipients.Count == 0) return 0;
        var alreadyInvited = await repo.GetInvitedUserIdsAsync(surveyId, ct);
        var completedPublicParticipants = (await repo.GetInvitationsAsync(surveyId, ct))
            .Where(invitation => invitation.SentAt is null && invitation.Completed)
            .Select(invitation => invitation.UserId);
        return recipients.Except(alreadyInvited).Except(completedPublicParticipants).Count();
    }

    public async Task<SendResult> SendInvitesAsync(Guid surveyId, Guid actorUserId, CancellationToken ct = default)
    {
        var survey = await repo.GetByIdAsync(surveyId, ct)
            ?? throw new InvalidOperationException("Survey not found.");
        if (survey.Status != SurveyStatus.Open)
            throw new InvalidOperationException("Invitations can only be sent for an Open survey.");
        var audienceType = survey.AudienceType
            ?? throw new InvalidOperationException("Survey has no audience to invite.");
        ValidateAudienceConfiguration(
            audienceType, survey.AudienceTeamId, survey.AudienceLoggedInSince, requireAudience: true);

        var now = clock.GetCurrentInstant();
        var target = await ResolveRecipientIdsAsync(
            audienceType, survey.AudienceTeamId, survey.AudienceLoggedInSince, ct);
        var alreadyInvited = await repo.GetInvitedUserIdsAsync(surveyId, ct);
        var participationRows = await repo.GetInvitationsAsync(surveyId, ct);
        var completedPublicParticipants = participationRows
            .Where(invitation => invitation.SentAt is null && invitation.Completed)
            .Select(invitation => invitation.UserId);
        var netNew = target
            .Except(alreadyInvited)
            .Except(completedPublicParticipants)
            .ToList();
        var existingParticipation = participationRows.ToDictionary(invitation => invitation.UserId);

        var emails = await userEmailService.GetNotificationTargetEmailsAsync(netNew, ct);
        var users = await userService.GetUserInfosAsync(netNew, ct);
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

            SurveyInvitation inv;
            if (existingParticipation.TryGetValue(userId, out var existing))
            {
                inv = existing;
                await repo.UpdateInvitationStatusAsync(
                    inv.Id, EmailOutboxStatus.Queued, now, ct);
            }
            else
            {
                inv = new SurveyInvitation
                {
                    Id = Guid.NewGuid(),
                    SurveyId = surveyId,
                    UserId = userId,
                    SentAt = now,
                    LatestEmailStatus = EmailOutboxStatus.Queued,
                    CreatedAt = now,
                };
                await repo.AddInvitationAndSaveAsync(inv, ct);
            }
            invitationsCreated++;

            var preferredCulture = users.TryGetValue(userId, out var user) ? user.PreferredLanguage : null;
            var culture = preferredCulture.IsSupportedCultureCode()
                ? preferredCulture!
                : survey.DefaultCulture;
            var name = user?.BurnerName ?? string.Empty;
            var title = survey.Title.Resolve(culture, survey.DefaultCulture);
            var customSubject = survey.InvitationEmailSubject.ResolveOptional(culture, survey.DefaultCulture);
            var customMessage = survey.InvitationEmailMessage.ResolveOptional(culture, survey.DefaultCulture);
            var token = tokenProvider.Create(inv.Id);
            var msg = emailMessages.SurveyInvitation(
                email, name, title, token, culture, customSubject, customMessage);

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

        await auditLog.LogAsync(AuditAction.SurveyInvitesSent, AuditEntityTypes.Survey, surveyId,
            $"Sent {invitationsCreated} invitation(s)", actorUserId);

        return new SendResult(invitationsCreated, emailsQueued, failed);
    }

    public async Task<int> SendDueRemindersAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var cutoff = now - Duration.FromDays(7);

        var due = await repo.GetInvitationsDueForReminderAsync(cutoff, ct);
        if (due.Count == 0) return 0;

        var userIds = due.Select(i => i.UserId).Distinct().ToList();
        var emails = await userEmailService.GetNotificationTargetEmailsAsync(userIds, ct);
        var users = await userService.GetUserInfosAsync(userIds, ct);

        // Loaded once per distinct survey (few open surveys at this scale). The answer window is
        // re-checked here rather than in the query: Open alone is not answerable, and reminding
        // someone about a survey past its ClosesAt sends them to the Closed page — and spends their
        // one ReminderSentAt stamp doing it.
        var surveys = new Dictionary<Guid, (string Title, string DefaultCulture, bool Answerable)>();

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

            if (!surveys.TryGetValue(inv.SurveyId, out var meta))
            {
                var survey = await repo.GetByIdAsync(inv.SurveyId, ct);
                if (survey is null) continue;
                meta = (
                    survey.Title.Resolve(survey.DefaultCulture, survey.DefaultCulture),
                    survey.DefaultCulture,
                    SurveyWizardFlow.IsAnswerable(survey.Status, survey.OpensAt, survey.ClosesAt, now));
                surveys[inv.SurveyId] = meta;
            }

            if (!meta.Answerable) continue;

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

        await auditLog.LogAsync(AuditAction.SurveyReminderSent, AuditEntityTypes.Survey, Guid.Empty,
            $"Sent {reminded} survey reminder(s)", jobName: AuditEntityTypes.ReminderJob);

        return reminded;
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
        var draftAnswers = draft is null
            ? (IReadOnlyList<SurveyDraftAnswer>)[]
            : draft.Answers
                .Select(a => new SurveyDraftAnswer(
                    a.QuestionId,
                    a.SelectedOptionValues,
                    a.TextValue,
                    a.RatingValue,
                    a.GridSelections?.ToDictionary(
                        kv => kv.Key,
                        kv => (IReadOnlyList<string>)kv.Value,
                        StringComparer.Ordinal)))
                .ToList();

        return new SurveyAnswerContext(
            invitation.SurveyId,
            invitation.Id,
            invitation.UserId,
            definition,
            draftAnswers,
            HasResumableDraft: draft is not null,
            IsEligible: isEligible);
    }

    public async Task<bool> IsEligibleAsociadoAsync(Guid userId, CancellationToken ct = default)
        => IsEligibleAsociado(await userService.GetUserInfoAsync(userId, ct));

    public async Task<Guid> StartIdentifiedDraftAsync(
        Guid surveyId,
        Guid participationId,
        Guid userId,
        SurveyInputMethod inputMethod,
        string culture,
        CancellationToken ct = default)
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
            return existing.Id;
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
        return response.Id;
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

        Guid? draftResponseId = null;
        IReadOnlyList<SurveyDraftAnswer> draftAnswers = [];
        if (anonymity == ResponseAnonymity.Identified)
        {
            var existingDraft = await repo.GetDraftResponseAsync(surveyId, userId, ct);
            draftResponseId = await StartIdentifiedDraftAsync(
                surveyId, participation.Id, userId, SurveyInputMethod.Slug, culture, ct);
            draftAnswers = existingDraft is null ? [] : MapDraftAnswers(existingDraft);
        }
        return new SurveyPublicStart(participation.Id, draftResponseId, draftAnswers);
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

    public Task SaveDraftAnswersAsync(
        Guid draftResponseId,
        IReadOnlyList<SurveyAnswerInput> answers,
        SurveyInputMethod inputMethod,
        string culture,
        CancellationToken ct = default)
        => repo.SaveDraftAnswersAsync(
            draftResponseId,
            MapAnswers(draftResponseId, answers),
            inputMethod,
            culture,
            ct);

    public async Task SubmitResponseAsync(SurveySubmission submission, CancellationToken ct = default)
    {
        var prepared = await PrepareSubmissionAsync(submission, ct);
        if (prepared.AlreadyCompleted)
        {
            throw new InvalidOperationException("This invitation has already submitted a response.");
        }
        if (prepared.MissingRequired.Count > 0)
        {
            throw new InvalidOperationException("Required survey questions are unanswered.");
        }

        await PersistResponseAsync(submission, prepared.VisibleAnswers, ct);
    }

    private async Task<SubmissionPreparation> PrepareSubmissionAsync(
        SurveySubmission submission, CancellationToken ct)
    {
        // Load one authoritative definition for branching, normalization, required validation, and
        // persistence preparation. The caller must persist the returned answers without reloading it.
        var survey = await repo.GetByIdAsync(submission.SurveyId, ct)
            ?? throw new InvalidOperationException("Survey not found.");

        // The service is the authoritative gate: the controller's window checks are UX, this one is the rule.
        if (!SurveyWizardFlow.IsAnswerable(survey.Status, survey.OpensAt, survey.ClosesAt, clock.GetCurrentInstant()))
        {
            throw new InvalidOperationException("Survey is not open for responses.");
        }
        if (survey.IsAsociadoVote == true)
        {
            if (submission.Anonymity != ResponseAnonymity.Identified || submission.UserId is not { } userId)
            {
                throw new InvalidOperationException(
                    "Asociado votes require an identified eligible Asociado.");
            }
            if (!await IsEligibleAsociadoAsync(userId, ct))
            {
                throw new InvalidOperationException(
                    "Only an active, approved Asociado may answer this vote.");
            }
        }

        // Same authoritative posture for the duplicate gate: a completed invitation can't submit
        // again on the tracked tiers (Anonymous never flips Completed, so it is unaffected).
        if (submission.Anonymity != ResponseAnonymity.Anonymous && submission.InvitationId is { } gateInvId)
        {
            var invitation = await repo.GetInvitationByIdAsync(gateInvId, ct);
            if (invitation?.Completed == true)
            {
                return new SubmissionPreparation([], [], [], AlreadyCompleted: true);
            }
        }

        // Drop answers to questions hidden under full branching (defends against tampered/stale posts).
        var visibleAnswers = VisibleAnswers(survey, submission.Answers);
        var questions = ToQuestionInputs(survey);
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

        return new SubmissionPreparation(visibleAnswers, questions, missingRequired, AlreadyCompleted: false);
    }

    private async Task PersistResponseAsync(
        SurveySubmission submission,
        IReadOnlyList<SurveyAnswerInput> visibleAnswers,
        CancellationToken ct)
    {
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
        var posted = postedAnswers.ToDictionary(a => a.QuestionId);

        foreach (var question in visibleBefore)
        {
            if (question.Type == SurveyQuestionType.Information) continue;
            var id = question.Id!.Value;
            if (!posted.TryGetValue(id, out var answer))
            {
                state.Answers.Remove(id.ToString());
                continue;
            }

            state.Answers[id.ToString()] = new SurveyWizardAnswer
            {
                SelectedOptionValues = answer.SelectedOptionValues.Where(v => !string.IsNullOrEmpty(v)).ToList(),
                GridSelections = NormalizeGridSelections(
                    question.GridRows,
                    question.Options,
                    question.GridSelectionMode,
                    answer.GridSelections),
                TextValue = string.IsNullOrWhiteSpace(answer.TextValue) ? null : answer.TextValue,
                RatingValue = answer.RatingValue,
                RankedValue = question.Type == SurveyQuestionType.RankedChoice
                    ? NormalizeRankedAnswer(question, answer.RankedValue)
                    : null,
            };
        }

        // A survey may be edited while a respondent has a wizard session open. Re-normalize every
        // stored Grid answer against the current schema before autosave, validation, navigation, or
        // submission so removed cells cannot survive and newly required rows cannot be bypassed.
        foreach (var question in editable.Questions.Where(q => q.Type == SurveyQuestionType.Grid && q.Id is not null))
        {
            if (!state.Answers.TryGetValue(question.Id!.Value.ToString(), out var answer)) continue;
            answer.GridSelections = NormalizeGridSelections(
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
        if (prepared.AlreadyCompleted)
        {
            return new SurveyWizardAdvanceResult(SurveyWizardOutcome.Submitted, []);
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

    public async Task<SurveyResultsView?> GetResultsAsync(Guid surveyId, CancellationToken ct = default)
    {
        var scoped = await GetScopedResultsAsync(surveyId, SurveyResultsScope.Combined, ct);
        return scoped is { IsEmbargoed: false } ? scoped.Results : null;
    }

    public async Task<SurveyScopedResults?> GetScopedResultsAsync(
        Guid surveyId,
        SurveyResultsScope scope,
        CancellationToken ct = default)
    {
        var survey = await repo.GetByIdAsync(surveyId, ct);
        if (survey is null) return null;

        var culture = survey.DefaultCulture;
        var responses = await repo.GetResponsesForResultsAsync(surveyId, ct);
        var embargoed = survey.IsAsociadoVote == true && survey.Status != SurveyStatus.Closed;
        var selectedResponses = responses
            .Where(response => MatchesScope(response.Anonymity, scope))
            .ToList();
        var invited = await repo.GetInvitedCountsBySurveyAsync(ct);

        var invitedCount = invited.GetValueOrDefault(surveyId);
        var responseCount = responses.Count;
        var completedInvitationCount = invitedCount == 0
            ? 0
            : (await repo.GetInvitationsAsync(surveyId, ct))
                .Count(invitation => invitation.SentAt.HasValue && invitation.Completed);
        var responseRate = invitedCount == 0 ? 0d : (double)completedInvitationCount / invitedCount;

        var questions = embargoed
            ? []
            : survey.Questions
                .Where(q => q.Type != SurveyQuestionType.Information)
                .OrderBy(q => q.PageNumber).ThenBy(q => q.Order)
                .Select(q => BuildQuestionAggregate(q, selectedResponses, culture))
                .ToList();

        var funnel = new SurveyFunnel(
            LinkStarted: await repo.GetStartedInvitationCountAsync(surveyId, ct),
            LinkFinished: responses.Count(r => r.InputMethod == SurveyInputMethod.UserSpecificLink),
            SlugStarted: survey.PublicStartedCount,
            SlugFinished: responses.Count(r => r.InputMethod == SurveyInputMethod.Slug));

        var identified = embargoed
            ? []
            : await BuildIdentifiedRespondentsAsync(survey, responses, culture, ct);
        var rankedQuestions = embargoed
            ? new Dictionary<Guid, RankedQuestionResult>()
            : survey.Questions
                .Where(question => question.Type == SurveyQuestionType.RankedChoice)
                .ToDictionary(
                    question => question.Id,
                    question => BuildRankedQuestionResult(question, selectedResponses, culture));

        return new SurveyScopedResults(
            new SurveyResultsView(
                surveyId,
                survey.Title.Resolve(culture, culture),
                survey.Status,
                invitedCount,
                responseCount,
                responseRate,
                funnel,
                questions,
                identified),
            embargoed ? 0 : selectedResponses.Count,
            scope,
            embargoed,
            rankedQuestions);
    }

    private static RankedQuestionResult BuildRankedQuestionResult(
        SurveyQuestion question,
        IReadOnlyList<SurveyResponse> responses,
        string culture)
    {
        var options = question.Options.OrderBy(option => option.Order).ToList();
        var authored = options.Select(option => option.Value).ToList();
        var unavailable = (question.RankedUnavailableOptionValues ?? [])
            .Where(value => authored.Contains(value, StringComparer.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var active = authored.Where(value => !unavailable.Contains(value)).ToHashSet(StringComparer.Ordinal);
        var labels = options.ToDictionary(
            option => option.Value,
            option => option.Label.Resolve(culture, culture),
            StringComparer.Ordinal);
        var ballots = responses
            .SelectMany(response => response.Answers)
            .Where(answer => answer.QuestionId == question.Id && answer.RankedValue is not null)
            .Select(answer => new RankedBallot(
                answer.RankedValue!.RankGroups,
                answer.RankedValue.Rejected.ToHashSet(StringComparer.Ordinal)))
            .ToList();
        RankedMethodResult Method(string name, string? winner, bool tieBreak) => new(
            name,
            winner,
            winner is null ? null : labels.GetValueOrDefault(winner, winner),
            tieBreak);

        (
            RankedPairwiseMatrix Matrix,
            RankedMethodResult Official,
            IReadOnlyList<RankedMethodResult> Methods,
            IReadOnlyList<string> PreferenceCycle)
            Count(IReadOnlySet<string>? candidates)
        {
            var matrix = RankedChoiceCounter.BuildPairwise(authored, ballots, candidates);
            var rankedPairs = RankedChoiceCounter.CountRankedPairs(authored, matrix, candidates);
            var condorcet = RankedChoiceCounter.CheckCondorcet(authored, matrix, candidates);
            var borda = RankedChoiceCounter.CountBorda(authored, ballots, candidates);
            var official = Method("Ranked Pairs (official)", rankedPairs.Winner, rankedPairs.TieBreakUsed);
            return (
                matrix,
                official,
                [
                    official,
                    Method("Condorcet check", condorcet.Winner, false),
                    Method("Borda Count", borda.Winner, borda.TieBreakUsed),
                ],
                condorcet.SmallestCycle);
        }

        var original = Count(null);
        var current = Count(active);

        return new RankedQuestionResult(
            options.Select(option => new RankedCandidateResult(
                option.Value,
                labels[option.Value],
                !unavailable.Contains(option.Value),
                ballots.Count(ballot => ballot.Rejected.Contains(option.Value)),
                ballots.Count == 0
                    ? 0d
                    : 100d * ballots.Count(ballot => ballot.Rejected.Contains(option.Value)) / ballots.Count))
                .ToList(),
            original.Official,
            current.Official,
            current.Methods,
            current.Matrix.Contests,
            original.PreferenceCycle,
            current.PreferenceCycle,
            unavailable.ToList());
    }

    public async Task SetRankedAvailabilityAsync(
        Guid surveyId,
        Guid questionId,
        IReadOnlyList<string> unavailableValues,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        var detail = await GetForEditAsync(surveyId, ct)
            ?? throw new InvalidOperationException("Survey not found.");
        if (detail.Status != SurveyStatus.Closed)
            throw new InvalidOperationException("Candidate availability can only change after the vote closes.");
        var question = detail.Editable.Questions.FirstOrDefault(candidate => candidate.Id == questionId);
        if (question?.Type != SurveyQuestionType.RankedChoice)
            throw new InvalidOperationException("Ranked-choice question not found.");
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
        await UpdateCoreAsync(
            surveyId,
            detail.Editable with { Questions = updatedQuestions },
            actorUserId,
            allowRankedAvailabilityChanges: true,
            ct);
    }

    private static bool MatchesScope(ResponseAnonymity anonymity, SurveyResultsScope scope) => scope switch
    {
        SurveyResultsScope.Unique => anonymity is ResponseAnonymity.Identified or ResponseAnonymity.CompletionTracked,
        SurveyResultsScope.Anonymous => anonymity == ResponseAnonymity.Anonymous,
        _ => true,
    };

    public async Task<SurveyResponseExport?> GetResponseExportAsync(Guid surveyId, CancellationToken ct = default)
    {
        var survey = await repo.GetByIdAsync(surveyId, ct);
        if (survey is null) return null;
        if (survey.IsAsociadoVote == true && survey.Status != SurveyStatus.Closed) return null;

        var culture = survey.DefaultCulture;
        var responses = await repo.GetResponsesForResultsAsync(surveyId, ct);

        var orderedQuestions = survey.Questions
            .Where(q => q.Type != SurveyQuestionType.Information)
            .OrderBy(q => q.PageNumber).ThenBy(q => q.Order)
            .ToList();

        var questions = orderedQuestions
            .Select(q => new SurveyExportQuestion(
                q.Id,
                q.Prompt.Resolve(culture, culture),
                q.Type,
                q.Options.OrderBy(o => o.Order)
                    .Select(o => new SurveyExportOption(o.Value, o.Label.Resolve(culture, culture)))
                    .ToList(),
                q.GridSelectionMode,
                 q.GridRows?.Select(row => new SurveyExportGridRow(
                     row.Value,
                     row.Label.Resolve(culture, culture))).ToList(),
                 ToRankedSettings(q.RankedSettings),
                 q.RankedUnavailableOptionValues?.ToList()))
            .ToList();

        var questionsById = survey.Questions.ToDictionary(q => q.Id);
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
            ? new Dictionary<Guid, UserInfo>()
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
                        a.RatingValue,
                        CopyGridSelections(a),
                         questionsById.TryGetValue(a.QuestionId, out var question)
                             ? ResolveGridSelections(a, question, culture)
                             : [],
                         CopyRankedBallot(a)))
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
                var questionsById = survey?.Questions.ToDictionary(q => q.Id)
                    ?? new Dictionary<Guid, SurveyQuestion>();

                return new
                {
                    Survey = survey?.Title.Resolve(culture, culture) ?? r.SurveyId.ToString(),
                    SubmittedAt = r.SubmittedAt.ToIso8601(),
                    Culture = culture,
                    Answers = r.Answers.Select(a => new
                    {
                        Question = prompts.GetValueOrDefault(a.QuestionId, string.Empty),
                        SelectedLabels = ResolveSelectedLabels(a, optionLabels),
                        GridSelections = CopyGridSelections(a),
                        GridSelectionLabels = questionsById.TryGetValue(a.QuestionId, out var question)
                             ? ResolveGridSelections(a, question, culture)
                             : [],
                        RankedBallot = CopyRankedBallot(a),
                        RankedBallotLabels = ResolveRankedBallot(a, optionLabels),
                        a.TextValue,
                        a.RatingValue,
                    }).ToList(),
                };
            })
            .ToList();

        return [new UserDataSlice(GdprExportSections.SurveyResponses, shaped)];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [GdprExportSections.SurveyResponses] =
                "Partially retained: the invitation is deleted and the response is severed from " +
                "the person (UserId and InvitationId dropped, Anonymity forced to Anonymous), but " +
                "the answers themselves survive as an anonymous data point in the survey's " +
                "results — GDPR Art. 17(3)(b). They are no longer attributable to anyone."
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>
    /// The identity link is dropped and the response demoted to Anonymous — the
    /// answers survive as anonymous research data that is no longer personal data.
    /// </summary>
    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        repo.AnonymizeResponsesForUserAsync(userId, ct);

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

            case SurveyQuestionType.Grid:
                {
                    var columns = question.Options
                        .OrderBy(option => option.Order)
                        .Select(option => new SurveyExportOption(
                            option.Value,
                            option.Label.Resolve(culture, culture)))
                        .ToList();
                    var rows = (question.GridRows ?? [])
                        .Select(row =>
                        {
                            var answeredCount = answers.Count(answer =>
                                answer.GridSelections?.TryGetValue(row.Value, out var selected) == true
                                && selected.Count > 0);
                            var cells = columns.Select(column =>
                            {
                                var count = answers.Count(answer =>
                                    answer.GridSelections?.TryGetValue(row.Value, out var selected) == true
                                    && selected.Contains(column.Value, StringComparer.Ordinal));
                                var percent = answeredCount == 0
                                    ? 0d
                                    : (double)count / answeredCount * 100d;
                                return new GridCellCount(column.Value, column.Label, count, percent);
                            }).ToList();
                            return new GridAggregateRow(
                                row.Value,
                                row.Label.Resolve(culture, culture),
                                cells);
                        })
                        .ToList();
                    var grid = new GridAggregate(
                        question.GridSelectionMode ?? GridSelectionMode.Single,
                        columns,
                        rows);
                    return new QuestionAggregate(question.Id, prompt, question.Type, [], [], null, [], grid);
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
            .OrderBy(r => r.SubmittedAt)
            .ThenBy(r => r.Id)
            .ToList();
        if (identified.Count == 0) return [];

        var userIds = identified.Select(r => r.UserId!.Value).Distinct().ToList();
        var users = await userService.GetUserInfosAsync(userIds, ct);

        var optionLabels = survey.Questions.ToDictionary(
            q => q.Id,
            q => q.Options.ToDictionary(o => o.Value, o => o.Label.Resolve(culture, culture), StringComparer.Ordinal));
        var prompts = survey.Questions.ToDictionary(q => q.Id, q => q.Prompt.Resolve(culture, culture));
        var questionsById = survey.Questions.ToDictionary(q => q.Id);

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
                        a.RatingValue,
                         questionsById.TryGetValue(a.QuestionId, out var question)
                             ? ResolveGridSelections(a, question, culture)
                             : [],
                         ResolveRankedBallot(a, optionLabels)))
                    .ToList();
                return new RespondentDetail(userId, name, r.SubmittedAt, answers);
            })
            .ToList();
    }

    private static IReadOnlyList<string> ResolveSelectedLabels(
        SurveyAnswer answer, IReadOnlyDictionary<Guid, Dictionary<string, string>> optionLabels)
    {
        if (answer.SelectedOptionValues.Count == 0) return [];
        var labels = optionLabels.GetValueOrDefault(answer.QuestionId);
        return answer.SelectedOptionValues
            .Select(v => labels is not null && labels.TryGetValue(v, out var label) ? label : v)
            .ToList();
    }

    private static IReadOnlyList<ResolvedGridSelection> ResolveGridSelections(
        SurveyAnswer answer, SurveyQuestion question, string culture)
    {
        if (answer.GridSelections is null) return [];

        var rows = (question.GridRows ?? []).ToDictionary(
            row => row.Value,
            row => row.Label.Resolve(culture, culture),
            StringComparer.Ordinal);
        var columns = question.Options.ToDictionary(
            option => option.Value,
            option => option.Label.Resolve(culture, culture),
            StringComparer.Ordinal);
        return answer.GridSelections
            .Where(selection => selection.Value.Count > 0)
            .Select(selection => new ResolvedGridSelection(
                selection.Key,
                rows.GetValueOrDefault(selection.Key, selection.Key),
                selection.Value.ToList(),
                selection.Value
                    .Select(value => columns.GetValueOrDefault(value, value))
                    .ToList()))
            .ToList();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>>? CopyGridSelections(SurveyAnswer answer)
        => answer.GridSelections?.ToDictionary(
            selection => selection.Key,
            selection => (IReadOnlyList<string>)selection.Value.ToList(),
            StringComparer.Ordinal);

    private static SurveyRankedSettings? ToRankedSettings(RankedQuestionSettings? settings)
        => settings is null
            ? null
            : new SurveyRankedSettings(
                settings.AllowEqualRanks,
                settings.AllowReject,
                settings.OfficialMethod.ToString());

    private static SurveyRankedBallot? CopyRankedBallot(SurveyAnswer answer)
        => answer.RankedValue is null
            ? null
            : new SurveyRankedBallot(
                [.. answer.RankedValue.RankGroups.Select(group => (IReadOnlyList<string>)[.. group])],
                [.. answer.RankedValue.Rejected]);

    private static ResolvedRankedBallot? ResolveRankedBallot(
        SurveyAnswer answer,
        IReadOnlyDictionary<Guid, Dictionary<string, string>> optionLabels)
    {
        if (answer.RankedValue is null) return null;

        var labels = optionLabels.GetValueOrDefault(answer.QuestionId);
        string Resolve(string value) =>
            labels is not null && labels.TryGetValue(value, out var label) ? label : value;

        return new ResolvedRankedBallot(
            [.. answer.RankedValue.RankGroups
                .Select(group => (IReadOnlyList<string>)[.. group.Select(Resolve)])],
            [.. answer.RankedValue.Rejected.Select(Resolve)]);
    }

    private static IReadOnlyList<QuestionInput> ToQuestionInputs(Survey survey)
        => survey.Questions
            .OrderBy(question => question.PageNumber)
            .ThenBy(question => question.Order)
            .Select(question => new QuestionInput(
                question.Id,
                question.PageNumber,
                question.Order,
                question.Type,
                question.Prompt,
                question.HelpText,
                question.IsRequired,
                question.RatingMin,
                question.RatingMax,
                question.RatingMinLabel,
                question.RatingMaxLabel,
                question.ShowIf,
                question.Options
                    .OrderBy(option => option.Order)
                    .Select(option => new OptionInput(
                        option.Id,
                        option.Order,
                        option.Value,
                        option.Label))
                    .ToList(),
                question.GridSelectionMode,
                question.GridRows?
                    .Select(row => new GridRowInput(row.Value, row.Label))
                    .ToList(),
                question.InformationImages?
                    .Select(image => new InformationImageInput(
                        image.Id,
                        image.Label,
                        image.AltText,
                        image.StoragePath,
                        image.ContentType,
                        image.FileName))
                    .ToList(),
                question.RankedSettings,
                question.RankedUnavailableOptionValues))
            .ToList();

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
    private static IReadOnlyList<SurveyAnswerInput> VisibleAnswers(Survey survey, IReadOnlyList<SurveyAnswerInput> answers)
    {
        var states = answers.ToDictionary(
            a => a.QuestionId,
            a => new AnswerState(a.SelectedOptionValues, a.TextValue, a.RatingValue, a.GridSelections, a.RankedValue));

        var effective = SurveyBranchingEvaluator.EffectiveAnswerStates(
            survey.Questions
                .OrderBy(q => q.PageNumber).ThenBy(q => q.Order)
                .Select(q => (q.Id, q.ShowIf)),
            states);

        var questions = survey.Questions.ToDictionary(q => q.Id);
        return answers
            .Where(a => effective.ContainsKey(a.QuestionId))
            .Where(a => questions.TryGetValue(a.QuestionId, out var question)
                && question.Type != SurveyQuestionType.Information)
            .Select(a =>
            {
                var question = questions[a.QuestionId];
                var normalizedGridSelections = question.Type == SurveyQuestionType.Grid
                    ? NormalizeGridSelections(
                        question.GridRows?.Select(row => new GridRowInput(row.Value, row.Label)).ToList(),
                        question.Options
                            .OrderBy(option => option.Order)
                            .Select(option => new OptionInput(option.Id, option.Order, option.Value, option.Label))
                            .ToList(),
                        question.GridSelectionMode,
                        a.GridSelections)
                    : null;
                var normalizedRanked = question.Type == SurveyQuestionType.RankedChoice
                    ? NormalizeRankedAnswer(question, a.RankedValue)
                    : null;
                return a with
                {
                    GridSelections = normalizedGridSelections?.Count > 0
                        ? normalizedGridSelections.ToDictionary(
                                kv => kv.Key,
                                kv => (IReadOnlyList<string>)kv.Value,
                                StringComparer.Ordinal)
                        : null,
                    RankedValue = normalizedRanked,
                };
            })
            .ToList();
    }

    private sealed record SubmissionPreparation(
        IReadOnlyList<SurveyAnswerInput> VisibleAnswers,
        IReadOnlyList<QuestionInput> Questions,
        IReadOnlyList<Guid> MissingRequired,
        bool AlreadyCompleted);

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

    /// <summary>
    /// Resolves an audience predicate into the set of recipient user ids via cross-section read
    /// interfaces. No marketing opt-out filter — surveys are System/always-send.
    /// </summary>
    private async Task<IReadOnlySet<Guid>> ResolveRecipientIdsAsync(
        SurveyAudienceType type, Guid? teamId, Instant? loggedInSince, CancellationToken ct)
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

            case SurveyAudienceType.Asociados:
                return (await userService.GetAllUserInfosAsync(ct))
                    .Where(IsEligibleAsociado)
                    .Select(user => user.Id)
                    .ToHashSet();

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

            case SurveyAudienceType.LoggedInSince:
                {
                    // "Logged in on or after the cutoff" = LastLoginAt >= cutoff; null LastLoginAt never
                    // matches (predates tracking). Deliberately no IsApproved filter — mid-onboarding
                    // users belong in this audience (nobodies-collective/Humans#894) — but tombstones
                    // (GDPR-anonymized/merged), deletion-pending users, and accounts walled off by
                    // state (rejected/suspended — they can't reach the survey) are never invited.
                    if (loggedInSince is null) return new HashSet<Guid>();
                    var users = await userService.GetAllUserInfosAsync(ct);
                    return users
                        .Where(u => u.LastLoginAt is { } lastLogin && lastLogin >= loggedInSince.Value)
                        .Where(u => !u.IsGdprAnonymized && !u.IsDeletionPending && !u.IsMerged)
                        .Where(u => u.State is not (UserState.Rejected or UserState.Suspended or UserState.AdminSuspended))
                        .Select(u => u.Id)
                        .ToHashSet();
                }

            default:
                // Unknown audience type resolves to nobody, silently — the send would look like
                // it worked while inviting no one.
                logger.SwitchDefaultWarn(type);
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

    private static bool IsEligibleAsociado(UserInfo? user)
        => user is
        {
            IsApproved: true,
            Profile.MembershipTier: MembershipTier.Asociado,
            IsGdprAnonymized: false,
            IsDeletionPending: false,
            IsMerged: false,
            State: UserState.Active,
        };

    private static void ValidateAudienceConfiguration(
        SurveyAudienceType? type, Guid? teamId, Instant? loggedInSince, bool requireAudience)
    {
        var error = AudienceConfigurationError(type, teamId, loggedInSince, requireAudience);
        if (error is not null)
            throw new InvalidOperationException(error);
    }

    private static string? AudienceConfigurationError(
        SurveyAudienceType? type, Guid? teamId, Instant? loggedInSince, bool requireAudience)
    {
        if (type is null)
            return requireAudience ? "Survey has no audience to invite." : null;
        if (!Enum.IsDefined(type.Value))
            return "The selected survey audience is not supported.";
        if (type == SurveyAudienceType.Team && teamId is null)
            return "A team is required for the Team audience.";
        if (type == SurveyAudienceType.LoggedInSince && loggedInSince is null)
            return "A cutoff date is required for the Logged in since audience.";
        return null;
    }

    private static LocalizedText NormalizeLocalizedText(LocalizedText text)
        => new(text.Values.ToDictionary(
            pair => pair.Key,
            pair => pair.Value?.Trim() ?? string.Empty,
            StringComparer.OrdinalIgnoreCase));

    private static void ValidateInvitationEmailCopy(
        LocalizedText subject,
        LocalizedText message)
    {
        var multilineSubject = subject.Values.FirstOrDefault(
            pair => pair.Value.Contains('\r', StringComparison.Ordinal)
                    || pair.Value.Contains('\n', StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(multilineSubject.Key))
        {
            throw new InvalidOperationException(
                $"Survey invitation email subjects must be a single line ({multilineSubject.Key}).");
        }

        ValidateLocalizedLength(
            subject,
            InvitationEmailSubjectMaxLength,
            "Survey invitation email subjects");
        ValidateLocalizedLength(
            message,
            InvitationEmailMessageMaxLength,
            "Survey invitation email messages");

        static void ValidateLocalizedLength(LocalizedText text, int maxLength, string description)
        {
            var offender = text.Values.FirstOrDefault(pair => pair.Value.Length > maxLength);
            if (!string.IsNullOrEmpty(offender.Key))
            {
                throw new InvalidOperationException(
                    $"{description} must be {maxLength} characters or fewer ({offender.Key}).");
            }
        }
    }

    private async Task<PreparedInformationImages> PrepareInformationImagesAsync(
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
                    throw new InvalidOperationException(
                        $"An Information item can have at most {MaxInformationImages} images.");
                }

                var preparedImages = new List<InformationImageInput>(requestedImages.Count);
                foreach (var requested in requestedImages)
                {
                    if (requested.Upload is { } upload)
                    {
                        ValidateInformationImage(upload);
                        // Replacements get a fresh key so a failed database save cannot delete or
                        // overwrite the still-authoritative existing file.
                        var imageId = Guid.NewGuid();
                        var extension = Path.GetExtension(upload.FileName);
                        var storagePath = $"uploads/surveys/{surveyId}/{questionId}/{imageId}{extension}";
                        await fileStorage.SaveAsync(storagePath, upload.Content, ct);
                        newStoragePaths.Add(storagePath);
                        preparedImages.Add(requested with
                        {
                            Id = imageId,
                            StoragePath = storagePath,
                            ContentType = upload.ContentType,
                            FileName = upload.FileName,
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

                    throw new InvalidOperationException(
                        "Select an image file for every image row. " +
                        "If a previous save failed, select the file again.");
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
            await DeleteFilesBestEffortAsync(newStoragePaths, CancellationToken.None);
            throw;
        }

        return new PreparedInformationImages(
            input with { Questions = preparedQuestions },
            newStoragePaths);
    }

    private static void ValidateInformationImage(SurveyImageUpload upload)
    {
        if (upload.Length <= 0)
        {
            throw new InvalidOperationException("The selected image is empty.");
        }
        if (!AllowedInformationImageContentTypes.Contains(upload.ContentType))
        {
            throw new InvalidOperationException("Only JPEG, PNG, and WebP images are allowed.");
        }
        if (upload.Length > MaxInformationImageBytes)
        {
            throw new InvalidOperationException("Each Information image must be under 10 MB.");
        }
        if (!AllowedInformationImageExtensions.Contains(Path.GetExtension(upload.FileName)))
        {
            throw new InvalidOperationException(
                "Image filenames must end in .jpg, .jpeg, .png, or .webp.");
        }
    }

    private async Task DeleteFilesBestEffortAsync(
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

    private sealed record PreparedInformationImages(
        SurveyEditInput Input,
        IReadOnlyList<string> NewStoragePaths);

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
                GridSelectionMode = q.Type == SurveyQuestionType.Grid ? q.GridSelectionMode : null,
                GridRows = q.Type == SurveyQuestionType.Grid
                    ? (q.GridRows ?? []).Select(row => new SurveyGridRow(row.Value, row.Label)).ToList()
                    : null,
                InformationImages = q.Type == SurveyQuestionType.Information
                    ? (q.InformationImages ?? []).Select(image => new SurveyInformationImage(
                        image.Id!.Value,
                        image.StoragePath!,
                        image.ContentType!,
                        image.FileName!,
                        image.Label,
                        image.AltText)).ToList()
                    : null,
                RankedSettings = q.Type == SurveyQuestionType.RankedChoice
                    ? q.RankedSettings ?? RankedQuestionSettings.Default
                    : null,
                RankedUnavailableOptionValues = q.Type == SurveyQuestionType.RankedChoice
                    ? (q.RankedUnavailableOptionValues ?? []).Distinct(StringComparer.Ordinal).ToList()
                    : null,
                ShowIf = q.ShowIf,
                Options = q.Options.Select(o => new SurveyQuestionOption
                {
                    Id = o.Id ?? Guid.NewGuid(),
                    QuestionId = questionId,
                    Order = o.Order,
                    Value = o.Value,
                    Label = o.Label,
                }).ToList(),
            };
        }).ToList();

    private static void ValidateQuestionConfiguration(IReadOnlyList<SurveyQuestion> questions)
    {
        foreach (var question in questions)
        {
            if (question.Type == SurveyQuestionType.Information)
            {
                ValidateInformationQuestion(question);
                continue;
            }

            if (question.Type == SurveyQuestionType.RankedChoice)
            {
                ValidateRankedQuestion(question);
                continue;
            }

            if (question.Type != SurveyQuestionType.Grid) continue;

            ValidateGridQuestion(question);
        }

        static void ValidateInformationQuestion(SurveyQuestion question)
        {
            if (question.IsRequired)
                throw new InvalidOperationException($"Information item {question.Id} cannot be required.");

            var images = question.InformationImages ?? [];
            if (images.Count > MaxInformationImages)
            {
                throw new InvalidOperationException(
                    $"Information item {question.Id} can have at most {MaxInformationImages} images.");
            }

            if (!question.HelpText.Values.Values.Any(value => !string.IsNullOrWhiteSpace(value))
                && images.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Information item {question.Id} must contain Markdown or at least one image.");
            }

            if (images.Any(image =>
                !image.Label.Values.Values.Any(value => !string.IsNullOrWhiteSpace(value))))
            {
                throw new InvalidOperationException(
                    $"Every image in Information item {question.Id} must have a label.");
            }

            if (images.Any(image =>
                !image.AltText.Values.Values.Any(value => !string.IsNullOrWhiteSpace(value))))
            {
                throw new InvalidOperationException(
                    $"Every image in Information item {question.Id} must have alt text.");
            }
        }

        static void ValidateGridQuestion(SurveyQuestion question)
        {
            if (question.GridSelectionMode is null
                || !Enum.IsDefined(question.GridSelectionMode.Value))
            {
                throw new InvalidOperationException($"Grid question {question.Id} must choose a selection mode.");
            }

            var rows = question.GridRows ?? [];
            if (rows.Count == 0)
                throw new InvalidOperationException($"Grid question {question.Id} must have at least one row.");

            if (question.Options.Count == 0 || question.Options.Count > 5)
            {
                throw new InvalidOperationException(
                    $"Grid question {question.Id} must have between one and five columns.");
            }

            ValidateStableValues(rows.Select(row => row.Value), $"Grid question {question.Id} row");
            ValidateStableValues(question.Options.Select(option => option.Value), $"Grid question {question.Id} column");
        }

        static void ValidateRankedQuestion(SurveyQuestion question)
        {
            if (question.Options.Count < 2)
                throw new InvalidOperationException($"Ranked-choice question {question.Id} must have at least two options.");
            ValidateStableValues(
                question.Options.OrderBy(option => option.Order).Select(option => option.Value),
                $"Ranked-choice question {question.Id} option");
            if (question.RankedSettings is null
                || !Enum.IsDefined(question.RankedSettings.OfficialMethod))
            {
                throw new InvalidOperationException(
                    $"Ranked-choice question {question.Id} must have valid ranking settings.");
            }
        }

        static void ValidateStableValues(IEnumerable<string> values, string description)
        {
            var materialized = values.ToList();
            if (materialized.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidOperationException($"{description} values must not be blank.");
            }

            if (materialized.Distinct(StringComparer.Ordinal).Count() != materialized.Count)
            {
                throw new InvalidOperationException($"{description} values must be unique.");
            }
        }
    }

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

        var types = questions.ToDictionary(question => question.Id, question => question.Type);
        var nonAnswerSources = questions
            .Where(question => question.ShowIf?.Clauses.Any(clause =>
                types.GetValueOrDefault(clause.QuestionId) is
                    SurveyQuestionType.Grid or SurveyQuestionType.Information or SurveyQuestionType.RankedChoice) == true)
            .Select(question => question.Id)
            .ToList();
        if (nonAnswerSources.Count > 0)
        {
            throw new InvalidOperationException(
                $"Grid, RankedChoice, and Information questions cannot be branching sources. Offending question ids: {string.Join(", ", nonAnswerSources)}.");
        }
    }

    private static void ValidateVoteConfiguration(SurveyEditInput input)
    {
        if (!input.IsAsociadoVote) return;
        if (input.AudienceType != SurveyAudienceType.Asociados)
            throw new InvalidOperationException("Asociado votes must target the Asociados audience.");
        if (input.AllowAnonymous)
            throw new InvalidOperationException("Asociado votes must use identified responses.");
        if (!string.IsNullOrWhiteSpace(input.PublicSlug))
            throw new InvalidOperationException("Asociado votes cannot have a public link.");
    }

    private static void ValidateRankedDefinitionFrozen(
        IEnumerable<SurveyQuestion> existing,
        IEnumerable<SurveyQuestion> updated)
    {
        var oldRanked = existing
            .Where(question => question.Type == SurveyQuestionType.RankedChoice)
            .OrderBy(question => question.PageNumber).ThenBy(question => question.Order)
            .ToList();
        var newRanked = updated
            .Where(question => question.Type == SurveyQuestionType.RankedChoice)
            .OrderBy(question => question.PageNumber).ThenBy(question => question.Order)
            .ToList();
        if (oldRanked.Count != newRanked.Count)
            throw new InvalidOperationException("Ranked-choice questions cannot be added or removed after voting starts.");
        for (var index = 0; index < oldRanked.Count; index++)
        {
            var before = oldRanked[index];
            var after = newRanked[index];
            var beforeValues = before.Options.OrderBy(option => option.Order).Select(option => option.Value);
            var afterValues = after.Options.OrderBy(option => option.Order).Select(option => option.Value);
            if (before.Id != after.Id
                || !beforeValues.SequenceEqual(afterValues, StringComparer.Ordinal)
                || before.RankedSettings != after.RankedSettings)
            {
                throw new InvalidOperationException(
                    "Ranked-choice candidates, order, and settings cannot change after the first saved answer.");
            }
        }
    }

    private static RankedAnswer? NormalizeRankedAnswer(QuestionInput question, RankedAnswer? answer)
    {
        if (answer is null) return null;
        var authored = question.Options.OrderBy(option => option.Order).Select(option => option.Value).ToList();
        return NormalizeRankedAnswer(authored, question.RankedSettings, answer);
    }

    private static RankedAnswer? NormalizeRankedAnswer(SurveyQuestion question, RankedAnswer? answer)
    {
        if (answer is null) return null;
        var authored = question.Options.OrderBy(option => option.Order).Select(option => option.Value).ToList();
        return NormalizeRankedAnswer(authored, question.RankedSettings, answer);
    }

    private static RankedAnswer? NormalizeRankedAnswer(
        IReadOnlyList<string> authored,
        RankedQuestionSettings? settings,
        RankedAnswer answer)
    {
        settings ??= RankedQuestionSettings.Default;
        var known = authored.ToHashSet(StringComparer.Ordinal);
        var suppliedKnownValues = answer.RankGroups
            .SelectMany(group => group)
            .Concat(answer.Rejected)
            .Where(known.Contains)
            .ToList();
        if (suppliedKnownValues.Distinct(StringComparer.Ordinal).Count() != suppliedKnownValues.Count)
            throw new InvalidOperationException("A ranked-choice option may appear only once in a ballot.");

        var authoredPositions = authored
            .Select((value, index) => (value, index))
            .ToDictionary(pair => pair.value, pair => pair.index, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<IReadOnlyList<string>>();
        foreach (var group in answer.RankGroups)
        {
            var valid = group
                .Where(value => known.Contains(value) && seen.Add(value))
                .OrderBy(value => authoredPositions[value])
                .ToList();
            if (!settings.AllowEqualRanks && valid.Count > 1)
                throw new InvalidOperationException("This ranked-choice question does not allow equal ranks.");
            if (valid.Count > 0) groups.Add(valid);
        }

        var rejected = answer.Rejected
            .Where(value => known.Contains(value) && seen.Add(value))
            .OrderBy(value => authoredPositions[value])
            .ToList();
        if (!settings.AllowReject && rejected.Count > 0)
            throw new InvalidOperationException("This ranked-choice question does not allow rejection.");
        return groups.Count == 0 && rejected.Count == 0 ? null : new RankedAnswer(groups, rejected);
    }

    private static Dictionary<string, List<string>> NormalizeGridSelections(
        IReadOnlyList<GridRowInput>? rows,
        IReadOnlyList<OptionInput> columns,
        GridSelectionMode? mode,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? selections)
    {
        if (rows is null || mode is null || selections is null)
        {
            return new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }

        var rowValues = rows.Select(row => row.Value).ToHashSet(StringComparer.Ordinal);
        var columnValues = columns.Select(column => column.Value).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (rowValue, selectedColumns) in selections)
        {
            if (!rowValues.Contains(rowValue)) continue;
            var valid = selectedColumns
                .Where(columnValues.Contains)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (mode == GridSelectionMode.Single && valid.Count > 1)
            {
                valid = valid.Take(1).ToList();
            }

            if (valid.Count > 0) result[rowValue] = valid;
        }

        return result;
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
