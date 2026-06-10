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
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Web.Controllers;

/// <summary>
/// Public survey answering wizard. Two entry paths share one page flow: the tokenised invite link
/// (<c>/Survey/Answer?t=…</c>), where identity comes from the token's invitation (never the current
/// principal); and the public slug link (<c>/Survey/{slug}</c>), which is always Anonymous and carries
/// no identity. Controllers parse → call the service → format (hard rule): all flow decisions live in
/// <see cref="ISurveyService.AdvanceWizardAsync"/>; the controller persists the session per
/// <see cref="WizardRoute"/> and renders/redirects per the outcome.
/// </summary>
[AllowAnonymous]
[Route("Survey")]
public class SurveyController(
    ISurveyService surveyService,
    IUserServiceRead userService,
    IClock clock,
    IStringLocalizer<SharedResource> localizer,
    ILogger<SurveyController> logger) : HumansControllerBase(userService)
{
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

        if (!IsAnswerable(ctx.Definition))
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

            resumeState.CurrentPage = SurveyWizardFlow.FirstVisiblePage(
                editable.Questions, SurveyWizardFlow.ToAnswerStates(resumeState.Answers)) ?? 0;
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

        // Re-gate on the POST: the intro may have been loaded just before the window closed.
        if (!IsAnswerable(ctx.Definition))
        {
            return View("Closed", new SurveyClosedViewModel { Reason = "closed" });
        }

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
            editable.Questions, SurveyWizardFlow.ToAnswerStates(state.Answers)) ?? 0;

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

        // The service returns null for unknown slugs and for surveys that no longer allow anonymous.
        var ctx = await surveyService.ResolvePublicContextAsync(slug, ct);
        if (ctx is null) return NotFound();

        var editable = ctx.Definition.Editable;

        if (!IsAnswerable(ctx.Definition))
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

        if (!IsAnswerable(ctx.Definition))
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

        // Mirror the invited Start action: record the public start at intro advance (not the first page
        // POST) so the SlugStarted funnel counts visitors who click Start and abandon on the first page.
        state.Started = true;
        await surveyService.IncrementPublicStartedAsync(ctx.SurveyId, ct);

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
        return View("ThankYou", BuildThankYou(ctx?.Definition));
    }

    [HttpGet("Answer/ThankYou")]
    public async Task<IActionResult> ThankYou(string t, CancellationToken ct)
    {
        // The wizard session is already cleared; re-resolve the survey (token still valid) for the copy.
        var ctx = await surveyService.ResolveAnswerContextAsync(t, ct);
        return View("ThankYou", BuildThankYou(ctx?.Definition));
    }

    /// <summary>
    /// Routing/side-effect differences between the two entry paths, so the shared page flow stays
    /// path-agnostic. The invited path saves session by token and redirects to <c>Page</c>/<c>ThankYou</c>;
    /// the public path saves by slug and redirects to <c>PublicPage</c>/<c>PublicThankYou</c>.
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

    /// <summary>Entry/page UX gate (the service re-enforces the same rule authoritatively at submit).</summary>
    private bool IsAnswerable(SurveyDetail definition)
        => SurveyWizardFlow.IsAnswerable(
            definition.Status, definition.Editable.OpensAt, definition.Editable.ClosesAt,
            clock.GetCurrentInstant());

    /// <summary>
    /// Shared GET page render for both entry paths. Re-gates Open/closes-at, skips a now-all-hidden page,
    /// and renders <c>Page.cshtml</c>; <paramref name="route"/> carries the token-vs-slug differences.
    /// </summary>
    private async Task<IActionResult> RenderPage(SurveyWizardState state, WizardRoute route, CancellationToken ct)
    {
        var definition = await surveyService.GetForEditAsync(state.SurveyId, ct);
        if (definition is null) return View("Closed", new SurveyClosedViewModel { Reason = "invalid" });

        var editable = definition.Editable;
        if (!IsAnswerable(definition))
        {
            return View("Closed", new SurveyClosedViewModel { Reason = "closed" });
        }

        var answerStates = SurveyWizardFlow.ToAnswerStates(state.Answers);

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
    /// Shared POST page processor for both entry paths: parse the posted answers, let the service
    /// advance the wizard, persist the session, and render/redirect per the outcome.
    /// </summary>
    private async Task<IActionResult> ProcessPage(
        SurveyWizardState state, SurveyPageInputModel model, WizardRoute route, CancellationToken ct)
    {
        var posted = (model.Answers ?? [])
            .Select(a => new SurveyAnswerInput(
                a.QuestionId, a.SelectedOptionValues ?? [], a.TextValue, a.RatingValue))
            .ToList();

        var result = await surveyService.AdvanceWizardAsync(state, model.Page, model.Back, posted, ct);

        switch (result.Outcome)
        {
            case SurveyWizardOutcome.NotFound:
                return View("Closed", new SurveyClosedViewModel { Reason = "invalid" });

            case SurveyWizardOutcome.Closed:
                return View("Closed", new SurveyClosedViewModel { Reason = "closed" });

            case SurveyWizardOutcome.ValidationFailed:
                foreach (var id in result.MissingRequired)
                {
                    ModelState.AddModelError(id.ToString(), localizer["Survey_QuestionRequired"]);
                }

                route.Save(HttpContext.Session, state);
                return await RenderPage(state, route, ct);

            case SurveyWizardOutcome.Submitted:
                route.Clear(HttpContext.Session);
                return RedirectToAction(route.ThankYouAction, route.PageRouteValues);

            case SurveyWizardOutcome.Navigated:
            default:
                route.Save(HttpContext.Session, state);
                return RedirectToAction(route.PageAction, route.PageRouteValues);
        }
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

    /// <summary>Resolves the thank-you copy (survey's own text, else the localized fallback).</summary>
    private SurveyThankYouViewModel BuildThankYou(SurveyDetail? definition)
    {
        if (definition is null)
        {
            return new SurveyThankYouViewModel { ThankYou = localizer["Survey_ThankYouFallback"] };
        }

        var editable = definition.Editable;
        var culture = ResolveCulture(editable.DefaultCulture);
        var thankYou = editable.ThankYou.Resolve(culture, editable.DefaultCulture);

        return new SurveyThankYouViewModel
        {
            Title = editable.Title.Resolve(culture, editable.DefaultCulture),
            ThankYou = string.IsNullOrWhiteSpace(thankYou) ? localizer["Survey_ThankYouFallback"] : thankYou,
        };
    }

    /// <summary>Picks a supported display culture, defaulting to the current UI culture then the survey's default.</summary>
    private static string ResolveCulture(string surveyDefault)
        => ResolveCulture(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, surveyDefault);

    private static string ResolveCulture(string? requested, string surveyDefault)
    {
        if (requested.IsSupportedCultureCode()) return requested!;
        if (surveyDefault.IsSupportedCultureCode()) return surveyDefault;
        return CultureCatalog.DefaultCultureCode;
    }

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
