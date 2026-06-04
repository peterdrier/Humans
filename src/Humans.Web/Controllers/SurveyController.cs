using System.Globalization;
using Humans.Application.Interfaces.Surveys;
using Humans.Application.Interfaces.Users;
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
            // Establish the wizard session from the resumable Identified draft.
            var resumeState = BuildState(ctx, ResponseAnonymity.Identified, culture);
            resumeState.Started = true;
            SurveyWizardSession.Save(HttpContext.Session, t, resumeState);

            // Task 4.2: redirect into the page flow — RedirectToAction("Page", new { t }).
            // Until Page exists, render the intro flagged as a resume.
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

        SurveyWizardSession.Save(HttpContext.Session, model.Token, state);

        // Task 4.2: the Page action does not exist yet — it is built in the next task.
        return RedirectToAction("Page", new { t = model.Token });
    }

    /// <summary>Builds the wizard session state from the answer context. Invitee identity is carried only for Identified.</summary>
    private static SurveyWizardState BuildState(SurveyAnswerContext ctx, ResponseAnonymity anonymity, string culture)
    {
        var identified = anonymity == ResponseAnonymity.Identified;
        return new SurveyWizardState
        {
            SurveyId = ctx.SurveyId,
            InvitationId = identified ? ctx.InvitationId : null,
            UserId = identified ? ctx.UserId : null,
            Anonymity = anonymity,
            InputMethod = SurveyInputMethod.UserSpecificLink,
            Culture = culture,
            CurrentPage = 0,
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
}
