using AwesomeAssertions;
using Humans.Base.Attributes;
using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Tests;

/// <summary>
/// Alarm for the failure mode behind nobodies-collective/Humans#1057.
///
/// <para>
/// Every analyzer here finds its marker attribute with
/// <c>Compilation.GetTypeByMetadataName</c> and returns from
/// <c>OnCompilationStart</c> when the lookup comes back <c>null</c> — no symbol
/// action registered, no diagnostic, green build. A name that stops matching
/// therefore retires HUM0015/HUM0016, HUM0033 and HUM0010/HUM0011 in silence.
/// The names are now derived from the attribute classes themselves (linked into
/// <c>Humans.Analyzers</c> by its csproj), which removes the drift; these tests
/// are what makes a regression loud.
/// </para>
///
/// <para>
/// The other analyzer tests stub their marker attribute inline, so the stub and
/// the analyzer agree with each other no matter what the real class says. These
/// tests instead compile the <b>production source file</b>, embedded verbatim
/// from <c>src/Humans.Base/Attributes/</c> by the test csproj, into the
/// referenced assembly — the same shape as production, where the attribute lives
/// outside the assembly under analysis. If the analyzers' compiled copy ever
/// diverges from that file — the link dropped, a literal re-inlined, a hand-made
/// duplicate — the attribute stops resolving and these tests go red.
/// </para>
///
/// <para>
/// Nothing here spells a namespace as a literal: the snippets build their
/// attribute references from <c>typeof(...).Namespace</c>, so moving the
/// attributes' namespace needs no edit in the analyzers <i>or</i> here.
/// </para>
/// </summary>
public class ArchitectureAttributeIdentityTests
{
    /// <summary>
    /// Reads a production attribute source file out of this assembly's embedded
    /// resources. The owning project has <c>ImplicitUsings</c> enabled and these
    /// snippets are parsed on their own, so the implicit <c>using System;</c> is
    /// restored — the same thing <c>Humans.Analyzers.csproj</c> does with a
    /// <c>&lt;Using Include="System" /&gt;</c> item.
    /// </summary>
    private static string ProductionSource(string fileName)
    {
        var resourceName = "ProductionAttributes." + fileName;
        using var stream =
            typeof(ArchitectureAttributeIdentityTests).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' is missing. It is declared in " +
                "Humans.Analyzers.Tests.csproj against src/Humans.Base/Attributes/ — " +
                "if the attributes moved, move the declaration with them rather than " +
                "dropping it, or the analyzers lose their only end-to-end check.");

        using var reader = new StreamReader(stream);
        return "using System;\n" + reader.ReadToEnd();
    }

    [HumansFact]
    public async Task Production_SurfaceBudgetAttribute_still_drives_HUM0015()
    {
        var ns = typeof(SurfaceBudgetAttribute).Namespace;
        var source = $$"""
            namespace Sample
            {
                [{{ns}}.SurfaceBudget(1)]
                public interface ISample
                {
                    void M1();
                    void M2();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SurfaceBudgetAnalyzer(),
            "Humans.Application",
            source,
            ProductionSource("SurfaceBudgetAttribute.cs"));

        diagnostics.Should().ContainSingle(d =>
            string.Equals(d.Id, SurfaceBudgetAnalyzer.OverBudgetDiagnosticId, StringComparison.Ordinal));
    }

    [HumansFact]
    public async Task Production_SurfaceBudgetAttribute_still_drives_HUM0016()
    {
        var ns = typeof(SurfaceBudgetAttribute).Namespace;
        var source = $$"""
            namespace Sample
            {
                [{{ns}}.SurfaceBudget(3)]
                public interface ISample
                {
                    void M1();
                    void M2();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SurfaceBudgetAnalyzer(),
            "Humans.Application",
            source,
            ProductionSource("SurfaceBudgetAttribute.cs"));

        diagnostics.Should().ContainSingle(d =>
            string.Equals(d.Id, SurfaceBudgetAnalyzer.SlackDiagnosticId, StringComparison.Ordinal));
    }

    [HumansFact]
    public async Task Production_ExternalWriteAttribute_still_drives_HUM0033()
    {
        var ns = typeof(ExternalWriteAttribute).Namespace;
        var source = $$"""
            namespace Microsoft.AspNetCore.Http
            {
                public abstract class HttpContext
                {
                    public System.Threading.CancellationToken RequestAborted { get; }
                }
            }

            namespace Microsoft.AspNetCore.Mvc
            {
                public abstract class ControllerBase
                {
                    public Microsoft.AspNetCore.Http.HttpContext HttpContext { get; } = null!;
                }

                public sealed class HttpPostAttribute : System.Attribute { }
            }

            namespace Humans.Base.Interfaces
            {
                public interface ISyncService
                {
                    [{{ns}}.ExternalWrite]
                    System.Threading.Tasks.Task SyncAsync(System.Threading.CancellationToken ct);
                }
            }

            namespace Humans.Web.Controllers
            {
                public sealed class SyncController : Microsoft.AspNetCore.Mvc.ControllerBase
                {
                    private readonly Humans.Base.Interfaces.ISyncService _sync = null!;

                    [Microsoft.AspNetCore.Mvc.HttpPost]
                    public System.Threading.Tasks.Task Execute() =>
                        _sync.SyncAsync(HttpContext.RequestAborted);
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new RequestScopedCancellationOnExternalWriteAnalyzer(),
            "Humans.Web",
            source,
            ProductionSource("ExternalWriteAttribute.cs"));

        diagnostics.Should().ContainSingle(d =>
            string.Equals(
                d.Id,
                RequestScopedCancellationOnExternalWriteAnalyzer.DiagnosticId,
                StringComparison.Ordinal));
    }

    [HumansFact]
    public async Task Production_ExpiresOnAttribute_still_drives_HUM0010()
    {
        var ns = typeof(ExpiresOnAttribute).Namespace;
        var source = $$"""
            namespace Sample
            {
                public class Container
                {
                    [{{ns}}.ExpiresOn("2020-01-01")]
                    public int Foo { get; set; }
                }

                public class Caller
                {
                    public int Read(Container c) => c.Foo;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ExpiresOnAnalyzer(),
            "Humans.Application",
            source,
            ProductionSource("ExpiresOnAttribute.cs"));

        diagnostics.Should().ContainSingle(d =>
            string.Equals(d.Id, ExpiresOnAnalyzer.UsageDiagnosticId, StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>[Grandfathered]</c> is the one marker that already failed loudly — an
    /// unresolved lookup snaps every grandfathered violation back to
    /// <see cref="DiagnosticSeverity.Error"/> and the build goes red. This pins
    /// that behaviour to the real attribute rather than to a stub, so the
    /// downgrade path is covered by the same end-to-end check as the rest.
    /// </summary>
    [HumansFact]
    public async Task Production_GrandfatheredAttribute_still_downgrades_a_violation()
    {
        var ns = typeof(GrandfatheredAttribute).Namespace;
        var source = $$"""
            namespace Humans.Base.Interfaces.Repositories
            {
                public interface IRepository { }
                public interface ICampRepository : IRepository { }
            }

            namespace Microsoft.AspNetCore.Mvc
            {
                public abstract class ControllerBase { }
            }

            namespace Humans.Web.Controllers
            {
                [{{ns}}.Grandfathered("HUM0014", "test", "2026-05-15", "test")]
                public sealed class CampsController : Microsoft.AspNetCore.Mvc.ControllerBase
                {
                    public CampsController(
                        Humans.Base.Interfaces.Repositories.ICampRepository repo) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new WebRepositoryInjectionAnalyzer(),
            "Humans.Web",
            source,
            ProductionSource("GrandfatheredAttribute.cs"));

        var hum0014 = diagnostics
            .Where(d => string.Equals(d.Id, WebRepositoryInjectionAnalyzer.DiagnosticId, StringComparison.Ordinal))
            .ToArray();

        hum0014.Should().ContainSingle();
        hum0014[0].Severity.Should().Be(DiagnosticSeverity.Warning);
    }
}
