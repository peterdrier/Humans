using System.Globalization;
using Humans.Application.Interfaces.Surveys;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Surveys;
using Humans.Domain.Enums;
using Humans.Web.Extensions;
using Humans.Web.Models;
using Humans.Web.Models.Survey;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Web.Controllers;

/// <summary>
/// Public survey answering wizard. Entry is a tokenised invite link (<c>/Survey/Answer?t=…</c>); the
/// invitee may be unauthenticated, so identity comes from the token's invitation — never from the
/// current principal. Controllers parse → call the service → format (hard rule). The page flow,
/// submit, and public-slug entry are later tasks (4.2–4.4).
/// </summary>
[AllowAnonymous]
[Route("Survey")]
public class SurveyController(
    ISurveyService surveyService,
    IUserServiceRead userService,
    ILogger<SurveyController> logger) : HumansControllerBase(userService)
{
    /// <summary>Default transparency note placeholder — Phase 7 finalises the copy.</summary>
    private const string TransparencyNote =
        "Your responses may be reviewed and analysed, including by automated tooling, to improve the collective.";

    [HttpGet("Answer")]
    public async Task<IActionResult> Answer(string t, CancellationToken ct)
    {
        var ctx = await surveyService.ResolveAnswerContextAsync(t, ct);
        if (ctx is null)
        {
            logger.LogInformation("Survey answer link could not be resolved (invalid or expired token).");
            return View("Closed", new SurveyClosedViewModel { Reason = "invalid" });
        }

        var editable = ctx.Definition.Editable;

        // Gate: survey must be Open, and within its closes-at window if one is set.
        var now = SystemClock.Instance.GetCurrentInstant();
        if (ctx.Definition.Status != SurveyStatus.Open ||
            (editable.ClosesAt is { } closesAt && now > closesAt))
        {
            return View("Closed", new SurveyClosedViewModel { Reason = "closed" });
        }

        var culture = ResolveCulture(editable.DefaultCulture);

        if (ctx.HasResumableDraft)
        {
            // Establish the wizard session from the resumable Identified draft and jump into the flow.
            var resumeState = BuildState(ctx, ResponseAnonymity.Identified, culture);
            resumeState.Started = true;
            resumeState.DraftResponseId = await surveyService.StartIdentifiedDraftAsync(
                ctx.SurveyId, ctx.InvitationId, ctx.UserId, culture, ct);
            foreach (var a in ctx.DraftAnswers)
            {
                resumeState.Answers[a.QuestionId.ToString()] = new SurveyWizardAnswer
                {
                    SelectedOptionValues = a.SelectedOptionValues.ToList(),
                    TextValue = a.TextValue,
                    RatingValue = a.RatingValue,
                };
            }

            resumeState.CurrentPage =
                SurveyWizardFlow.FirstVisiblePage(editable.Questions, ToAnswerStates(resumeState.Answers)) ?? 0;
            SurveyWizardSession.Save(HttpContext.Session, t, resumeState);
            return RedirectToAction("Page", new { t });
        }

        var vm = new SurveyIntroViewModel
        {
            Token = t,
            Title = editable.Title.Resolve(culture, editable.DefaultCulture),
            Intro = editable.Intro.Resolve(culture, editable.DefaultCulture),
            Culture = culture,
            AllowAnonymous = editable.AllowAnonymous,
            HasResumableDraft = ctx.HasResumableDraft,
            TransparencyNote = TransparencyNote,
        };
        return View("Intro", vm);
    }

    [HttpPost("Answer/Start")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start(SurveyStartViewModel model, CancellationToken ct)
    {
        var ctx = await surveyService.ResolveAnswerContextAsync(model.Token, ct);
        if (ctx is null)
        {
            return View("Closed", new SurveyClosedViewModel { Reason = "invalid" });
        }

        var editable = ctx.Definition.Editable;

        // When the survey forbids anonymity, the only tier is Identified.
        var anonymity = editable.AllowAnonymous ? model.Anonymity : ResponseAnonymity.Identified;
        var culture = ResolveCulture(model.Culture, editable.DefaultCulture);

        var state = BuildState(ctx, anonymity, culture);
        state.Started = true;

        if (anonymity == ResponseAnonymity.Identified)
        {
            state.DraftResponseId = await surveyService.StartIdentifiedDraftAsync(
                ctx.SurveyId, ctx.InvitationId, ctx.UserId, culture, ct);
        }

        // First advance past the intro flips the invitation's funnel Started flag (all invited tiers).
        await surveyService.MarkInvitationStartedAsync(ctx.InvitationId, ct);

        // Enter at the first page that has a visible question (0 ⇒ no questions / nothing visible).
        state.CurrentPage = SurveyWizardFlow.FirstVisiblePage(
            editable.Questions, ToAnswerStates(state.Answers)) ?? 0;

        SurveyWizardSession.Save(HttpContext.Session, model.Token, state);
        return RedirectToAction("Page", new { t = model.Token });
    }

    [HttpGet("Answer/Page")]
    public async Task<IActionResult> Page(string t, CancellationToken ct)
    {
        var state = SurveyWizardSession.Load(HttpContext.Session, t);
        if (state is null) return RedirectToAction("Answer", new { t });

        var definition = await surveyService.GetForEditAsync(state.SurveyId, ct);
        if (definition is null) return View("Closed", new SurveyClosedViewModel { Reason = "invalid" });

        var editable = definition.Editable;
        var now = SystemClock.Instance.GetCurrentInstant();
        if (definition.Status != SurveyStatus.Open || (editable.ClosesAt is { } closesAt && now > closesAt))
        {
            return View("Closed", new SurveyClosedViewModel { Reason = "closed" });
        }

        var answerStates = ToAnswerStates(state.Answers);

        // The current page may have become all-hidden since it was set; advance to the next visible one.
        var visible = SurveyWizardFlow.VisibleQuestionsOnPage(editable.Questions, state.CurrentPage, answerStates);
        if (visible.Count == 0)
        {
            var next = SurveyWizardFlow.NextVisiblePage(editable.Questions, state.CurrentPage, answerStates);
            if (next is null) return RedirectToAction("ThankYou", new { t });
            state.CurrentPage = next.Value;
            SurveyWizardSession.Save(HttpContext.Session, t, state);
            visible = SurveyWizardFlow.VisibleQuestionsOnPage(editable.Questions, state.CurrentPage, answerStates);
        }

        var vm = BuildPageViewModel(t, state, editable, visible, answerStates);
        return View("Page", vm);
    }

    [HttpPost("Answer/Page")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitPage(SurveyPageInputModel model, CancellationToken ct)
    {
        var state = SurveyWizardSession.Load(HttpContext.Session, model.Token);
        if (state is null) return RedirectToAction("Answer", new { t = model.Token });

        var definition = await surveyService.GetForEditAsync(state.SurveyId, ct);
        if (definition is null) return View("Closed", new SurveyClosedViewModel { Reason = "invalid" });

        var editable = definition.Editable;
        var now = SystemClock.Instance.GetCurrentInstant();
        if (definition.Status != SurveyStatus.Open || (editable.ClosesAt is { } closesAt && now > closesAt))
        {
            return View("Closed", new SurveyClosedViewModel { Reason = "closed" });
        }

        // Only accept answers for questions actually visible on the posted page (re-evaluated server-side).
        var visibleBefore = SurveyWizardFlow.VisibleQuestionsOnPage(
            editable.Questions, model.Page, ToAnswerStates(state.Answers));
        var visibleIds = visibleBefore.Select(q => q.Id!.Value).ToHashSet();
        var posted = (model.Answers ?? []).ToDictionary(a => a.QuestionId);

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
                SelectedOptionValues = (answer.SelectedOptionValues ?? []).Where(v => !string.IsNullOrEmpty(v)).ToList(),
                TextValue = string.IsNullOrWhiteSpace(answer.TextValue) ? null : answer.TextValue,
                RatingValue = answer.RatingValue,
            };
        }

        // First advance past the intro flips the invitation's funnel Started flag (idempotent safety net).
        if (!state.Started && state.InvitationId is { } startInvId)
        {
            await surveyService.MarkInvitationStartedAsync(startInvId, ct);
            state.Started = true;
        }

        var answerStates = ToAnswerStates(state.Answers);

        // Identified per-page autosave (replace-all; the draft stays in-progress).
        if (state.Anonymity == ResponseAnonymity.Identified && state.DraftResponseId is { } draftId)
        {
            await surveyService.SaveDraftAnswersAsync(draftId, MapAnswers(state.Answers), ct);
        }

        // Re-validate required-visible on this page; a Back navigation skips validation.
        var visibleAfter = SurveyWizardFlow.VisibleQuestionsOnPage(editable.Questions, model.Page, answerStates);
        if (!model.Back)
        {
            var missing = SurveyWizardFlow.RequiredUnanswered(visibleAfter, answerStates);
            if (missing.Count > 0)
            {
                foreach (var id in missing)
                {
                    ModelState.AddModelError(id.ToString(), "This question is required.");
                }

                state.CurrentPage = model.Page;
                SurveyWizardSession.Save(HttpContext.Session, model.Token, state);
                var errorVm = BuildPageViewModel(model.Token, state, editable, visibleAfter, answerStates);
                return View("Page", errorVm);
            }
        }

        if (model.Back)
        {
            var prev = PreviousVisiblePage(editable, model.Page, answerStates);
            state.CurrentPage = prev ?? model.Page;
            SurveyWizardSession.Save(HttpContext.Session, model.Token, state);
            return RedirectToAction("Page", new { t = model.Token });
        }

        var nextPage = SurveyWizardFlow.NextVisiblePage(editable.Questions, model.Page, answerStates);
        if (nextPage is not null)
        {
            state.CurrentPage = nextPage.Value;
            SurveyWizardSession.Save(HttpContext.Session, model.Token, state);
            return RedirectToAction("Page", new { t = model.Token });
        }

        // No further visible page ⇒ submit, then clear the wizard session.
        var submission = new SurveySubmission(
            state.SurveyId,
            state.InvitationId,
            state.Anonymity == ResponseAnonymity.Identified ? state.UserId : null,
            state.DraftResponseId,
            state.Anonymity,
            state.InputMethod,
            state.Culture,
            MapAnswers(state.Answers));
        await surveyService.SubmitResponseAsync(submission, ct);

        SurveyWizardSession.Clear(HttpContext.Session, model.Token);
        return RedirectToAction("ThankYou", new { t = model.Token });
    }

    [HttpGet("Answer/ThankYou")]
    public async Task<IActionResult> ThankYou(string t, CancellationToken ct)
    {
        // The wizard session is already cleared; re-resolve the survey (token still valid) for the copy.
        var ctx = await surveyService.ResolveAnswerContextAsync(t, ct);
        if (ctx is null)
        {
            return View("ThankYou", new SurveyThankYouViewModel { ThankYou = "Thank you for your response." });
        }

        var editable = ctx.Definition.Editable;
        var culture = ResolveCulture(editable.DefaultCulture);
        var thankYou = editable.ThankYou.Resolve(culture, editable.DefaultCulture);

        return View("ThankYou", new SurveyThankYouViewModel
        {
            Title = editable.Title.Resolve(culture, editable.DefaultCulture),
            ThankYou = string.IsNullOrWhiteSpace(thankYou) ? "Thank you for your response." : thankYou,
        });
    }

    /// <summary>
    /// Builds the wizard session state from the answer context. The token's invitation id and user id
    /// are carried for ALL invited tiers — they drive the funnel flags <c>Invitation.Started</c> (on
    /// first advance) and <c>Invitation.Completed</c> (at submit, Identified + CompletionTracked).
    /// Writing those identity columns onto the RESPONSE is Identified-only — that happens at submit.
    /// </summary>
    private static SurveyWizardState BuildState(SurveyAnswerContext ctx, ResponseAnonymity anonymity, string culture)
        => new()
        {
            SurveyId = ctx.SurveyId,
            InvitationId = ctx.InvitationId,
            UserId = ctx.UserId,
            Anonymity = anonymity,
            InputMethod = SurveyInputMethod.UserSpecificLink,
            Culture = culture,
            CurrentPage = 0,
        };

    /// <summary>Picks a supported display culture, defaulting to the current UI culture then the survey's default.</summary>
    private static string ResolveCulture(string surveyDefault)
        => ResolveCulture(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, surveyDefault);

    private static string ResolveCulture(string? requested, string surveyDefault)
    {
        if (requested.IsSupportedCultureCode()) return requested!;
        if (surveyDefault.IsSupportedCultureCode()) return surveyDefault;
        return CultureCatalog.DefaultCultureCode;
    }

    /// <summary>Projects the session answers into the flow's <see cref="AnswerState"/> map (keyed by question id).</summary>
    private static Dictionary<Guid, AnswerState> ToAnswerStates(IReadOnlyDictionary<string, SurveyWizardAnswer> answers)
    {
        var result = new Dictionary<Guid, AnswerState>();
        foreach (var (key, a) in answers)
        {
            if (Guid.TryParse(key, out var id))
            {
                result[id] = new AnswerState(a.SelectedOptionValues, a.TextValue, a.RatingValue);
            }
        }

        return result;
    }

    /// <summary>Maps the session answers to the service submission/autosave shape.</summary>
    private static IReadOnlyList<SurveyAnswerInput> MapAnswers(IReadOnlyDictionary<string, SurveyWizardAnswer> answers)
        => answers
            .Where(kv => Guid.TryParse(kv.Key, out _))
            .Select(kv => new SurveyAnswerInput(
                Guid.Parse(kv.Key),
                kv.Value.SelectedOptionValues,
                kv.Value.TextValue,
                kv.Value.RatingValue))
            .ToList();

    /// <summary>The nearest visible page strictly before <paramref name="page"/>, or null at the start.</summary>
    private static int? PreviousVisiblePage(SurveyEditInput editable, int page, IReadOnlyDictionary<Guid, AnswerState> answers)
        => SurveyWizardFlow.OrderedPages(editable.Questions)
            .Where(p => p < page && SurveyWizardFlow.VisibleQuestionsOnPage(editable.Questions, p, answers).Count > 0)
            .Cast<int?>()
            .LastOrDefault();

    /// <summary>Resolves a page's visible questions to display strings and counts the visible-page step position.</summary>
    private static SurveyPageViewModel BuildPageViewModel(
        string token, SurveyWizardState state, SurveyEditInput editable,
        IReadOnlyList<QuestionInput> visible, IReadOnlyDictionary<Guid, AnswerState> answers)
    {
        var visiblePages = SurveyWizardFlow.OrderedPages(editable.Questions)
            .Where(p => SurveyWizardFlow.VisibleQuestionsOnPage(editable.Questions, p, answers).Count > 0)
            .ToList();
        var step = visiblePages.IndexOf(state.CurrentPage);

        return new SurveyPageViewModel
        {
            Token = token,
            Page = state.CurrentPage,
            Title = editable.Title.Resolve(state.Culture, editable.DefaultCulture),
            StepNumber = step < 0 ? 1 : step + 1,
            TotalSteps = visiblePages.Count,
            CanGoBack = step > 0,
            IsLastStep = step >= 0 && step == visiblePages.Count - 1,
            Questions = visible.Select(q => BuildPageQuestion(q, state, editable)).ToList(),
        };
    }

    private static SurveyPageQuestion BuildPageQuestion(QuestionInput q, SurveyWizardState state, SurveyEditInput editable)
    {
        state.Answers.TryGetValue(q.Id!.Value.ToString(), out var prior);
        return new SurveyPageQuestion
        {
            Id = q.Id!.Value,
            Type = q.Type,
            Prompt = q.Prompt.Resolve(state.Culture, editable.DefaultCulture),
            HelpText = q.HelpText.Resolve(state.Culture, editable.DefaultCulture),
            IsRequired = q.IsRequired,
            RatingMin = q.RatingMin,
            RatingMax = q.RatingMax,
            RatingMinLabel = q.RatingMinLabel.Resolve(state.Culture, editable.DefaultCulture),
            RatingMaxLabel = q.RatingMaxLabel.Resolve(state.Culture, editable.DefaultCulture),
            Options = q.Options
                .Select(o => new SurveyPageOption(o.Value, o.Label.Resolve(state.Culture, editable.DefaultCulture)))
                .ToList(),
            SelectedOptionValues = prior?.SelectedOptionValues ?? [],
            TextValue = prior?.TextValue,
            RatingValue = prior?.RatingValue,
        };
    }
}
