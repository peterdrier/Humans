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
/// Public survey answering wizard. Two entry paths share one page flow: the tokenised invite link
/// (<c>/Survey/Answer?t=…</c>), where identity comes from the token's invitation (never the current
/// principal); and the public slug link (<c>/Survey/{slug}</c>), which is always Anonymous and carries
/// no identity. Controllers parse → call the service → format (hard rule). The shared page rendering
/// and submit live in <c>RenderPage</c>/<c>ProcessPage</c>; the two paths differ only in how they
/// load/save the wizard session and which side effect the first advance triggers.
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
            ShowAnonymitySelector = editable.AllowAnonymous,
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
        return await RenderPage(state, WizardRoute.Invited(t), ct);
    }

    [HttpPost("Answer/Page")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitPage(SurveyPageInputModel model, CancellationToken ct)
    {
        var state = SurveyWizardSession.Load(HttpContext.Session, model.Token);
        if (state is null) return RedirectToAction("Answer", new { t = model.Token });
        return await ProcessPage(state, model, WizardRoute.Invited(model.Token), ct);
    }

    [HttpGet("{slug}")]
    public async Task<IActionResult> Public(string slug, CancellationToken ct)
    {
        // Reserved words can never be a public slug; let the literal-segment actions own them.
        if (IsReservedSlug(slug)) return NotFound();

        var ctx = await surveyService.ResolvePublicContextAsync(slug, ct);
        if (ctx is null) return NotFound();

        var editable = ctx.Definition.Editable;
        // A slug only answers when anonymous responding is allowed; otherwise the link is not public.
        if (!editable.AllowAnonymous) return NotFound();

        var now = SystemClock.Instance.GetCurrentInstant();
        if (ctx.Definition.Status != SurveyStatus.Open || (editable.ClosesAt is { } closesAt && now > closesAt))
        {
            return View("Closed", new SurveyClosedViewModel { Reason = "closed" });
        }

        var culture = ResolveCulture(editable.DefaultCulture);
        var vm = new SurveyIntroViewModel
        {
            Title = editable.Title.Resolve(culture, editable.DefaultCulture),
            Intro = editable.Intro.Resolve(culture, editable.DefaultCulture),
            Culture = culture,
            AllowAnonymous = true,
            ShowAnonymitySelector = false, // public path is always Anonymous — no tier choice.
            IsPublic = true,
            Slug = ctx.Definition.Editable.PublicSlug ?? slug,
            TransparencyNote = TransparencyNote,
        };
        return View("Intro", vm);
    }

    [HttpPost("{slug}/Start")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PublicStart(string slug, string culture, CancellationToken ct)
    {
        if (IsReservedSlug(slug)) return NotFound();

        var ctx = await surveyService.ResolvePublicContextAsync(slug, ct);
        if (ctx is null) return NotFound();

        var editable = ctx.Definition.Editable;
        if (!editable.AllowAnonymous) return NotFound();

        var now = SystemClock.Instance.GetCurrentInstant();
        if (ctx.Definition.Status != SurveyStatus.Open || (editable.ClosesAt is { } closesAt && now > closesAt))
        {
            return View("Closed", new SurveyClosedViewModel { Reason = "closed" });
        }

        var resolvedCulture = ResolveCulture(culture, editable.DefaultCulture);
        var state = new SurveyWizardState
        {
            SurveyId = ctx.SurveyId,
            InvitationId = null,
            UserId = null,
            DraftResponseId = null,
            Anonymity = ResponseAnonymity.Anonymous,
            InputMethod = SurveyInputMethod.Slug,
            Culture = resolvedCulture,
            CurrentPage = SurveyWizardFlow.FirstVisiblePage(editable.Questions, new Dictionary<Guid, AnswerState>()) ?? 0,
        };

        SurveyWizardSession.SaveBySlug(HttpContext.Session, slug, state);
        return RedirectToAction("PublicPage", new { slug });
    }

    [HttpGet("{slug}/Page")]
    public async Task<IActionResult> PublicPage(string slug, CancellationToken ct)
    {
        if (IsReservedSlug(slug)) return NotFound();

        var state = SurveyWizardSession.LoadBySlug(HttpContext.Session, slug);
        if (state is null) return RedirectToAction("Public", new { slug });
        return await RenderPage(state, WizardRoute.Public(slug), ct);
    }

    [HttpPost("{slug}/Page")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PublicSubmitPage(string slug, SurveyPageInputModel model, CancellationToken ct)
    {
        if (IsReservedSlug(slug)) return NotFound();

        var state = SurveyWizardSession.LoadBySlug(HttpContext.Session, slug);
        if (state is null) return RedirectToAction("Public", new { slug });
        return await ProcessPage(state, model, WizardRoute.Public(slug), ct);
    }

    [HttpGet("{slug}/ThankYou")]
    public async Task<IActionResult> PublicThankYou(string slug, CancellationToken ct)
    {
        if (IsReservedSlug(slug)) return NotFound();

        // The wizard session is already cleared; re-resolve the survey for the closing copy.
        var ctx = await surveyService.ResolvePublicContextAsync(slug, ct);
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
    /// Routing/side-effect differences between the two entry paths, so the shared page flow stays
    /// path-agnostic. The invited path saves session by token, redirects to <c>Page</c>/<c>ThankYou</c>,
    /// and flips the invitation's <c>Started</c> flag on first advance; the public path saves by slug,
    /// redirects to <c>PublicPage</c>/<c>PublicThankYou</c>, and increments the survey's public counter.
    /// </summary>
    private sealed record WizardRoute(bool IsPublic, string Key)
    {
        public static WizardRoute Invited(string token) => new(false, token);

        public static WizardRoute Public(string slug) => new(true, slug);

        public string PageAction => IsPublic ? "PublicPage" : "Page";

        public string ThankYouAction => IsPublic ? "PublicThankYou" : "ThankYou";

        public object PageRouteValues => IsPublic ? new { slug = Key } : new { t = Key };

        public void Save(ISession session, SurveyWizardState state)
        {
            if (IsPublic) SurveyWizardSession.SaveBySlug(session, Key, state);
            else SurveyWizardSession.Save(session, Key, state);
        }

        public void Clear(ISession session)
        {
            if (IsPublic) SurveyWizardSession.ClearBySlug(session, Key);
            else SurveyWizardSession.Clear(session, Key);
        }
    }

    private static bool IsReservedSlug(string slug)
        => string.Equals(slug, "admin", StringComparison.OrdinalIgnoreCase)
           || string.Equals(slug, "answer", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Shared GET page render for both entry paths. Re-gates Open/closes-at, skips a now-all-hidden page,
    /// and renders <c>Page.cshtml</c>; <paramref name="route"/> carries the token-vs-slug differences.
    /// </summary>
    private async Task<IActionResult> RenderPage(SurveyWizardState state, WizardRoute route, CancellationToken ct)
    {
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
            if (next is null) return RedirectToAction(route.ThankYouAction, route.PageRouteValues);
            state.CurrentPage = next.Value;
            route.Save(HttpContext.Session, state);
            visible = SurveyWizardFlow.VisibleQuestionsOnPage(editable.Questions, state.CurrentPage, answerStates);
        }

        var vm = BuildPageViewModel(route, state, editable, visible, answerStates);
        return View("Page", vm);
    }

    /// <summary>
    /// Shared POST page processor for both entry paths. Captures the page's visible answers, fires the
    /// path-specific first-advance side effect, autosaves Identified drafts, validates required-visible,
    /// then navigates back/next or submits + clears the session. <paramref name="route"/> carries the
    /// token-vs-slug differences (session key, redirect actions, first-advance effect).
    /// </summary>
    private async Task<IActionResult> ProcessPage(
        SurveyWizardState state, SurveyPageInputModel model, WizardRoute route, CancellationToken ct)
    {
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

        // First advance past the intro fires the path-specific Started funnel side effect (idempotent via state.Started).
        if (!state.Started)
        {
            if (route.IsPublic)
            {
                await surveyService.IncrementPublicStartedAsync(state.SurveyId, ct);
            }
            else if (state.InvitationId is { } startInvId)
            {
                await surveyService.MarkInvitationStartedAsync(startInvId, ct);
            }

            state.Started = true;
        }

        var answerStates = ToAnswerStates(state.Answers);

        // Identified per-page autosave (replace-all; the draft stays in-progress). Slug path is Anonymous — skipped.
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
                route.Save(HttpContext.Session, state);
                var errorVm = BuildPageViewModel(route, state, editable, visibleAfter, answerStates);
                return View("Page", errorVm);
            }
        }

        if (model.Back)
        {
            var prev = PreviousVisiblePage(editable, model.Page, answerStates);
            state.CurrentPage = prev ?? model.Page;
            route.Save(HttpContext.Session, state);
            return RedirectToAction(route.PageAction, route.PageRouteValues);
        }

        var nextPage = SurveyWizardFlow.NextVisiblePage(editable.Questions, model.Page, answerStates);
        if (nextPage is not null)
        {
            state.CurrentPage = nextPage.Value;
            route.Save(HttpContext.Session, state);
            return RedirectToAction(route.PageAction, route.PageRouteValues);
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

        route.Clear(HttpContext.Session);
        return RedirectToAction(route.ThankYouAction, route.PageRouteValues);
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
        WizardRoute route, SurveyWizardState state, SurveyEditInput editable,
        IReadOnlyList<QuestionInput> visible, IReadOnlyDictionary<Guid, AnswerState> answers)
    {
        var visiblePages = SurveyWizardFlow.OrderedPages(editable.Questions)
            .Where(p => SurveyWizardFlow.VisibleQuestionsOnPage(editable.Questions, p, answers).Count > 0)
            .ToList();
        var step = visiblePages.IndexOf(state.CurrentPage);

        return new SurveyPageViewModel
        {
            Token = route.IsPublic ? string.Empty : route.Key,
            IsPublic = route.IsPublic,
            Slug = route.IsPublic ? route.Key : string.Empty,
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
