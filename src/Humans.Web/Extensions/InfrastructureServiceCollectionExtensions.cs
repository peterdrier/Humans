using Humans.Base;
using Humans.Base.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Base.Caching;
using Humans.Base.Services;
using Humans.Web.Services;
using Humans.Web.Extensions.Infrastructure;
using Humans.Web.Extensions.Sections;

namespace Humans.Web.Extensions;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddHumansInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        ConfigurationRegistry? configRegistry = null)
    {
        // MVC caches metadata across requests; defer resource lookups until binding
        // or rendering so each request's UI culture selects the message.
        services.AddOptions<MvcOptions>().Configure<IStringLocalizer<SharedResource>>((options, localizer) =>
        {
            var messages = options.ModelBindingMessageProvider;
            messages.SetMissingBindRequiredValueAccessor(_ => localizer["Validation_Required"]);
            messages.SetMissingKeyOrValueAccessor(() => localizer["Validation_Required"]);
            messages.SetMissingRequestBodyRequiredValueAccessor(() => localizer["Validation_Required"]);
            messages.SetValueMustNotBeNullAccessor(_ => localizer["Validation_Required"]);
            messages.SetAttemptedValueIsInvalidAccessor((_, _) => localizer["Validation_InvalidValue"]);
            messages.SetNonPropertyAttemptedValueIsInvalidAccessor(_ => localizer["Validation_InvalidValue"]);
            messages.SetUnknownValueIsInvalidAccessor(_ => localizer["Validation_InvalidValue"]);
            messages.SetNonPropertyUnknownValueIsInvalidAccessor(() => localizer["Validation_InvalidValue"]);
            messages.SetValueIsInvalidAccessor(_ => localizer["Validation_InvalidValue"]);
            messages.SetValueMustBeANumberAccessor(_ => localizer["Validation_Number"]);
            messages.SetNonPropertyValueMustBeANumberAccessor(() => localizer["Validation_Number"]);
        });

        services.AddConfigurationMetadata(configuration, configRegistry);
        services.AddTelemetryInfrastructure(configuration);
        services.AddEmailInfrastructure(configuration, environment);
        services.AddGoogleWorkspaceInfrastructure(configuration);
        services.AddTicketVendorPort(configuration);

        // Single key-addressed file storage rooted at wwwroot. Camps,
        // profile pictures, and any future file-bearing section share this
        // mount (production: Coolify volume at /app/wwwroot/uploads/).
        services.AddSingleton<IFileStorage, FileSystemFileStorage>();

        services.AddAuthSection();
        services.AddAdminSection();

        // GuideSettings and its GitHub fetcher stay in Base: three of IGuideContentSource's four consumers are not Guide's.
        services.Configure<GuideSettings>(configuration.GetSection(GuideSettings.SectionName));
        services.AddSingleton<IGuideContentSource, GitHubGuideContentSource>();

        services.AddDiscoveredSections(configuration);

        return services;
    }
}
