using System.Text;
using Humans.Application.Interfaces;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Humans.Integration.Tests.DependencyInjection;

public class DiResolutionSmokeTests : IClassFixture<HumansWebApplicationFactory>
{
    private static readonly HashSet<Type> RuntimeBootstrappedServiceTypes =
    [
        typeof(IImmediateOutboxProcessor)
    ];

    private readonly HumansWebApplicationFactory _factory;

    public DiResolutionSmokeTests(HumansWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact(Timeout = 60000)]
    public async Task All_application_registrations_resolve_from_a_real_app_scope()
    {
        await using var scope = _factory.Services.CreateAsyncScope();

        var failures = new List<string>();

        foreach (var group in _factory.RegisteredServices
                     .Where(IsResolvableApplicationRegistration)
                     .GroupBy(d => d.ServiceType)
                     .OrderBy(g => g.Key.FullName, StringComparer.Ordinal))
        {
            try
            {
                if (group.Count() == 1)
                {
                    var resolved = scope.ServiceProvider.GetRequiredService(group.Key);
                    if (resolved is null)
                        failures.Add($"{group.Key.FullName}: resolved null");
                }
                else
                {
                    var enumerableType = typeof(IEnumerable<>).MakeGenericType(group.Key);
                    var resolved = scope.ServiceProvider.GetRequiredService(enumerableType);
                    if (resolved is not System.Collections.ICollection collection || collection.Count < group.Count())
                    {
                        failures.Add(
                            $"{group.Key.FullName}: expected at least {group.Count()} registrations from IEnumerable<{group.Key.Name}>");
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{group.Key.FullName}: {ex.GetType().Name} - {ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            var message = new StringBuilder()
                .AppendLine("The following app registrations failed DI resolution:")
                .AppendJoin(Environment.NewLine, failures)
                .ToString();
            Assert.Fail(message);
        }
    }

    private static bool IsResolvableApplicationRegistration(ServiceDescriptor descriptor)
    {
        if (descriptor.IsKeyedService)
            return false;

        var serviceType = descriptor.ServiceType;
        if (RuntimeBootstrappedServiceTypes.Contains(serviceType))
            return false;

        if (serviceType.IsGenericTypeDefinition)
            return false;

        if (serviceType.IsConstructedGenericType &&
            serviceType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return false;

        var serviceNamespace = serviceType.Namespace ?? string.Empty;
        var implementationNamespace = descriptor.ImplementationType?.Namespace ?? string.Empty;

        var isOurService =
            serviceNamespace.StartsWith("Humans.", StringComparison.Ordinal) ||
            implementationNamespace.StartsWith("Humans.", StringComparison.Ordinal);

        if (!isOurService)
            return false;

        return serviceType != typeof(Program);
    }
}
