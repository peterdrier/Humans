using System.Reflection;
using System.Globalization;
using Humans.Base;
using Humans.Base.Configuration;
using Humans.Web.ModelBinders;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Primitives;
using NodaTime;
using Xunit;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Humans.Web.Tests.Infrastructure;

/// <summary>
/// Captive dependencies: a Singleton (or hosted service) that constructor-injects a Scoped
/// service. <c>Program.cs</c> builds the host with <c>ValidateScopes</c> /
/// <c>ValidateOnBuild</c>, so one of these throws at startup and the deployment answers 503
/// with nothing else failing first — the shipped section registrations are checked nowhere
/// else before a deploy. Singletons that need a Scoped service take
/// <c>IServiceScopeFactory</c> and open a scope per call (see <c>CachingCampService</c>).
/// </summary>
public sealed class ServiceProviderValidationTests
{
    [HumansTheory]
    [InlineData("https://example.com/", "/Account/MagicLinkConfirm")]
    [InlineData("https://example.com", "/Account/MagicLinkConfirm")]
    [InlineData("https://example.com/humans/", "/humans/Account/MagicLinkConfirm")]
    public void Email_link_base_does_not_duplicate_route_separators(string baseUrl, string expectedPath)
    {
        var configuration = BuildMinimalConfiguration();
        configuration["Email:BaseUrl"] = baseUrl;
        var registrations = new ServiceCollection();
        Extensions.Infrastructure.EmailInfrastructureExtensions.AddEmailInfrastructure(
            registrations, configuration, new StubHostEnvironment());
        using var services = registrations.BuildServiceProvider();
        var settings = services.GetRequiredService<IOptions<EmailSettings>>().Value;

        new Uri($"{settings.BaseUrl}/Account/MagicLinkConfirm").AbsolutePath.Should().Be(expectedPath);
    }

    [HumansFact]
    public void NoSingletonConstructorInjectsAScopedService()
    {
        var services = new ServiceCollection();
        Extensions.InfrastructureServiceCollectionExtensions
            .AddHumansInfrastructure(services, BuildMinimalConfiguration(), new StubHostEnvironment());

        var scopedServiceTypes = services
            .Where(d => d.Lifetime == ServiceLifetime.Scoped && !d.IsKeyedService)
            .Select(d => d.ServiceType)
            .ToHashSet();

        var captives = services
            .Where(d => d.Lifetime == ServiceLifetime.Singleton)
            .Select(d => d.IsKeyedService ? d.KeyedImplementationType : d.ImplementationType)
            .Where(t => t is not null)
            .Distinct()
            .SelectMany(t => Constructors(t!)
                .SelectMany(c => c.GetParameters())
                .Where(p => p.GetCustomAttribute<FromKeyedServicesAttribute>() is null)
                .Where(p => scopedServiceTypes.Contains(p.ParameterType))
                .Select(p => $"{t!.Name}.{p.Name} ({p.ParameterType.Name})"))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        captives.Should().BeEmpty(
            because: "a Singleton holding a Scoped service fails ValidateScopes at host "
                     + "startup — take IServiceScopeFactory and resolve per call instead");
    }

    [HumansTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("fr")]
    [InlineData("ca")]
    public async Task FormBindingErrors_UseTheCurrentRequestCulture(string culture)
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            var registrations = new ServiceCollection().AddLogging().AddLocalization();
            Extensions.InfrastructureServiceCollectionExtensions.AddHumansInfrastructure(
                registrations, BuildMinimalConfiguration(), new StubHostEnvironment());
            registrations.AddMvcCore(options =>
                options.ModelBinderProviders.Insert(0, new LocalDateTimeModelBinderProvider()))
                .AddViews().AddDataAnnotations();
            using var services = registrations.BuildServiceProvider();
            var metadataProvider = services.GetRequiredService<IModelMetadataProvider>();
            var binderFactory = services.GetRequiredService<IModelBinderFactory>();
            var localizer = services.GetRequiredService<IStringLocalizer<SharedResource>>();

            // Resolve MVC once, then change culture as successive requests do. Messages
            // must be localized when binding happens, not when options are constructed.
            foreach (var requestCulture in new[] { "en", culture })
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(requestCulture);
                var viewData = new ViewDataDictionary(metadataProvider, new ModelStateDictionary());
                var viewContext = new ViewContext
                {
                    HttpContext = new DefaultHttpContext { RequestServices = services },
                    ViewData = viewData,
                    ClientValidationEnabled = true,
                    FormContext = new FormContext(),
                };
                var numericInput = services.GetRequiredService<IHtmlGenerator>().GenerateTextBox(
                    viewContext, metadataProvider.GetModelExplorerForType(typeof(double), 1.5),
                    "Value", value: 1.5, format: null, htmlAttributes: null);
                var numberMessage = localizer["Validation_Number"];
                numberMessage.ResourceNotFound.Should().BeFalse();
                numericInput.Attributes["data-val-number"].Should().Be(numberMessage.Value);

                foreach (var (type, value, key) in new[]
                {
                    (typeof(int), "not-a-number", "Validation_InvalidValue"),
                    (typeof(int), "", "Validation_Required"),
                    (typeof(LocalDate?), "not-a-date", "Validation_InvalidValue"),
                    (typeof(LocalDateTime?), "not-a-date", "Validation_InvalidValue"),
                })
                {
                    var metadata = metadataProvider.GetMetadataForType(type);
                    var form = new FormCollection(new Dictionary<string, StringValues>(StringComparer.Ordinal)
                    {
                        ["Value"] = value,
                    });
                    var context = DefaultModelBindingContext.CreateBindingContext(
                        new ActionContext { HttpContext = new DefaultHttpContext { RequestServices = services } },
                        new FormValueProvider(BindingSource.Form, form, CultureInfo.InvariantCulture),
                        metadata, bindingInfo: null, modelName: "Value");
                    var binder = binderFactory.CreateBinder(new ModelBinderFactoryContext
                    {
                        Metadata = metadata,
                        CacheToken = type,
                    });

                    await binder.BindModelAsync(context);

                    context.Result.IsModelSet.Should().BeFalse();
                    var expected = localizer[key];
                    expected.ResourceNotFound.Should().BeFalse();
                    context.ModelState["Value"]!.Errors.Should().ContainSingle()
                        .Which.ErrorMessage.Should().Be(expected.Value);
                }
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    private static IEnumerable<ConstructorInfo> Constructors(Type type) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);

    private static IConfiguration BuildMinimalConfiguration()
    {
        var inMemory = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=stub;Username=stub;Password=stub",
            ["Email:FromAddress"] = "humans@nobodies.team",
            ["Email:BaseUrl"] = "https://localhost",
            ["Email:SmtpHost"] = "localhost",
            ["GitHub:Owner"] = "stub",
            ["GitHub:Repository"] = "stub",
            ["GitHub:AccessToken"] = "stub",
            ["GoogleMaps:ApiKey"] = "stub",
            ["TicketVendor:EventId"] = "stub-event",
            ["TicketVendor:Provider"] = "stub"
        };

        var builder = new ConfigurationBuilder();
        builder.Add(new MemoryConfigurationSource { InitialData = inMemory });
        return builder.Build();
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Humans.Web";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
