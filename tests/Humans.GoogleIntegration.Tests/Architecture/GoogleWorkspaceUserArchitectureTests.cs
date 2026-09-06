using AwesomeAssertions;
using Humans.GoogleIntegration.Services.Workspace;
using Humans.GoogleIntegration.Tests.Infrastructure;

namespace Humans.GoogleIntegration.Tests.Architecture;

/// <summary>
/// <see cref="IWorkspaceUserDirectoryClient"/> is the connector every Google SDK call for
/// Workspace accounts goes through: it must sit in the connector namespace and stay
/// shape-neutral.
/// </summary>
public class GoogleWorkspaceUserArchitectureTests
{
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
        // Every method parameter and return type must be the section's own or BCL —
        // never Google.Apis.*. Enforces the "shape-neutral" contract.
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
