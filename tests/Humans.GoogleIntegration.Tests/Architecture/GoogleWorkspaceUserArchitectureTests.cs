using AwesomeAssertions;
using GoogleWorkspaceUserService = Humans.GoogleIntegration.Services.GoogleWorkspaceUserService;
using Humans.GoogleIntegration.Services.Workspace;
using Humans.GoogleIntegration.Tests.Infrastructure;

namespace Humans.GoogleIntegration.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 pattern for the Google Integration
/// section's <see cref="GoogleWorkspaceUserService"/> — migrated under issue
/// #554 (split from the umbrella PR into an isolated sub-task). The service
/// now lives in <c>Humans.GoogleIntegration.Services</c> and
/// routes all Google SDK calls through
/// <see cref="IWorkspaceUserDirectoryClient"/>. These tests are the compile-
/// time guarantee that the connector boundary does not leak back into the
/// Application project.
/// </summary>
public class GoogleWorkspaceUserArchitectureTests
{
    // ── Application assembly cleanliness ─────────────────────────────────────

    [HumansFact]
    public void GoogleWorkspaceUserService_DoesNotReferenceGoogleSdkTypes() =>
        // Scoped to the type. The module-wide form was true while the service lived in
        // Humans.Application; the section now holds the connectors too.
        GoogleSdkContainment.AssertNamesNoGoogleSdkType(typeof(GoogleWorkspaceUserService));

    // ── IWorkspaceUserDirectoryClient ────────────────────────────────────────

    [HumansFact]
    public void IWorkspaceUserDirectoryClient_LivesInTheConnectorNamespace()
    {
        typeof(IWorkspaceUserDirectoryClient).Namespace
            .Should().Be(GoogleSdkContainment.ConnectorNamespace,
                because: "connector interfaces sit with their SDK-touching implementations, which is the section's Google-SDK boundary since the G5 move");
    }

    [HumansFact]
    public void IWorkspaceUserDirectoryClient_HasNoGoogleSdkTypesInSignatures()
    {
        // Every method parameter and return type must come from Humans.Application
        // or the BCL — never Google.Apis.*. Enforces the "shape-neutral" contract.
        var methods = typeof(IWorkspaceUserDirectoryClient).GetMethods();

        foreach (var method in methods)
        {
            var types = new[] { method.ReturnType }
                .Concat(method.GetParameters().Select(p => p.ParameterType))
                .SelectMany(UnwrapGenericArgs);

            foreach (var t in types)
            {
                (t.Namespace ?? string.Empty)
                    .Should().NotStartWith("Google.Apis",
                        because: $"{method.Name} leaks a Google SDK type through its signature; connector contracts must be shape-neutral");
            }
        }

        static IEnumerable<Type> UnwrapGenericArgs(Type t)
        {
            yield return t;
            if (t.IsGenericType)
            {
                foreach (var arg in t.GetGenericArguments())
                {
                    foreach (var inner in UnwrapGenericArgs(arg))
                        yield return inner;
                }
            }
        }
    }
}
