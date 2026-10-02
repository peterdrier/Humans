using System.Globalization;
using System.Resources;
using Humans.Surveys.Contracts;
using Humans.Email.Contracts;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Users.Contracts;

namespace Humans.Surveys.Services;

internal interface ISurveyPreviewEmailService : IOrchestrator
{
    Task<RenderedEmailPreview> PreviewForUserAsync(Guid surveyId, Guid userId, CancellationToken ct = default);
    Task<string> SendToUserAsync(Guid surveyId, Guid userId, CancellationToken ct = default);
}

/// <summary>
/// Sends a side-effect-free survey invitation preview to the requesting Board/Admin user. It reuses
/// the production invitation template and transport but creates no invitation, response, or funnel row.
/// </summary>
internal sealed class SurveyPreviewEmailService(
    ISurveyService surveyService,
    IUserEmailService userEmailService,
    IUserServiceRead userService,
    IEmailService emailService,
    SurveysEmails emailMessages,
    IEmailPreviewServiceRead emailPreviews,
    SurveyPreviewTokenProvider previewTokens,
    ILogger<SurveyPreviewEmailService> logger) : ISurveyPreviewEmailService
{
    private static readonly ResourceManager ErrorResources = new(typeof(SurveysResource));

    public async Task<RenderedEmailPreview> PreviewForUserAsync(
        Guid surveyId,
        Guid userId,
        CancellationToken ct = default)
    {
        var message = await BuildForUserAsync(surveyId, userId, ct);
        return emailPreviews.RenderSystemMessage(message);
    }

    public async Task<string> SendToUserAsync(Guid surveyId, Guid userId, CancellationToken ct = default)
    {
        var message = await BuildForUserAsync(surveyId, userId, ct);

        try
        {
            await emailService.SendAsync(message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to queue survey {SurveyId} preview email for requesting user {UserId}",
                surveyId, userId);
            throw new InvalidOperationException(ErrorResources.GetString("Surveys_PreviewQueueFailed", CultureInfo.CurrentUICulture)!, ex);
        }

        logger.LogInformation(
            "Survey {SurveyId} preview email queued for requesting user {UserId}",
            surveyId, userId);
        return message.RecipientEmail;
    }

    private async Task<EmailMessage> BuildForUserAsync(
        Guid surveyId,
        Guid userId,
        CancellationToken ct)
    {
        var detail = await surveyService.GetForEditAsync(surveyId, ct)
            ?? throw new InvalidOperationException(ErrorResources.GetString("Surveys_PreviewNotFound", CultureInfo.CurrentUICulture)!);
        var survey = detail.Editable;
        var emails = await userEmailService.GetNotificationTargetEmailsAsync([userId], ct);
        if (!emails.TryGetValue(userId, out var email))
            throw new InvalidOperationException(ErrorResources.GetString("Surveys_PreviewNoEmail", CultureInfo.CurrentUICulture)!);

        var user = await userService.GetUserInfoAsync(userId, ct);
        var name = user?.BurnerName ?? string.Empty;
        var culture = user?.PreferredLanguage.IsSupportedCultureCode() == true
            ? user.PreferredLanguage
            : survey.DefaultCulture;
        var title = survey.Title.Resolve(culture, survey.DefaultCulture);
        var customSubject = survey.InvitationEmailSubject.ResolveOptional(culture, survey.DefaultCulture);
        var customMessage = survey.InvitationEmailMessage.ResolveOptional(culture, survey.DefaultCulture);
        var token = previewTokens.Create(surveyId, culture);
        return emailMessages.SurveyInvitation(
            email, name, title, token, culture, customSubject, customMessage);
    }
}
