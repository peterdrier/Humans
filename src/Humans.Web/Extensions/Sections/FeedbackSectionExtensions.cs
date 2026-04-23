using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Repositories;
using Humans.Web.Filters;
using FeedbackApplicationService = Humans.Application.Services.Feedback.FeedbackService;

namespace Humans.Web.Extensions.Sections;

internal static class FeedbackSectionExtensions
{
    internal static IServiceCollection AddFeedbackSection(this IServiceCollection services)
    {
        // Feedback section — §15 repository + Application-layer service, no caching decorator.
        // Singleton + IDbContextFactory pattern (§15b) so the repository owns context lifetime.
        services.AddSingleton<IFeedbackRepository, FeedbackRepository>();
        services.AddScoped<FeedbackApplicationService>();
        services.AddScoped<IFeedbackService>(sp => sp.GetRequiredService<FeedbackApplicationService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<FeedbackApplicationService>());

        // Feedback API key
        services.Configure<FeedbackApiSettings>(opts =>
        {
            opts.ApiKey = Environment.GetEnvironmentVariable("FEEDBACK_API_KEY") ?? string.Empty;
        });
        services.AddScoped<ApiKeyAuthFilter>();

        return services;
    }
}
