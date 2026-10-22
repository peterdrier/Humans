using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Humans.Base.Attributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Humans.Analyzers;

/// <summary>
/// HUM0038 — a read path must not write. A controller action that answers GET
/// (or a view component's <c>Invoke</c>/<c>InvokeAsync</c>) must not call a
/// member of an <c>I*Write</c> interface or a method marked <c>[ExternalWrite]</c>.
/// </summary>
/// <remarks>
/// <para>
/// Rule: section-doctor's controller-intent lens, check 1 ("read path that
/// writes"); a controller may change how a fact looks, never what the fact is,
/// and a GET that changes state is changing the fact. Browsers prefetch links,
/// crawlers follow them and a GET carries no antiforgery token, so a write
/// reached from one runs whenever anyone loads the page.
/// </para>
/// <para>
/// <b>Why the write side is shaped this way.</b> Both signals are mechanical:
/// every member of an <c>I*Write</c> interface (the Published Write Surface)
/// is a write by the naming rule, and <c>[ExternalWrite]</c> marks a reach into
/// a third-party system in metadata. Mutating members of <c>I*Service</c> and
/// <c>I*Management</c> carry no such signal, so they are left to section-doctor
/// rather than guessed at from method names.
/// </para>
/// <para>
/// An action answers GET when it carries <c>[HttpGet]</c>, <c>[HttpHead]</c>, or an
/// <c>[AcceptVerbs]</c> listing GET or HEAD, or carries no verb attribute at all (MVC then routes every verb to it).
/// Only direct calls in the action body count, lambdas and local functions
/// included; a write laundered through a private controller helper is missed.
/// No <c>[Grandfathered]</c> downgrade: the rule shipped with zero violations.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ReadPathWriteAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0038";

    /// <summary>Derived from the attribute class, never a literal.</summary>
    public static readonly string ExternalWriteAttributeFullName =
        typeof(ExternalWriteAttribute).FullName!;

    private const string MvcNamespace = "Microsoft.AspNetCore.Mvc";
    private const string ControllerBaseFullName = "Microsoft.AspNetCore.Mvc.ControllerBase";
    private const string ViewComponentFullName = "Microsoft.AspNetCore.Mvc.ViewComponent";
    private const string WriteInterfaceSuffix = "Write";
    private const string HumansNamespacePrefix = "Humans";

    private static readonly ImmutableHashSet<string> ReadVerbAttributes =
        ImmutableHashSet.Create(StringComparer.Ordinal, "HttpGetAttribute", "HttpHeadAttribute");

    private static readonly ImmutableHashSet<string> VerbAttributes =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "HttpGetAttribute",
            "HttpHeadAttribute",
            "HttpPostAttribute",
            "HttpPutAttribute",
            "HttpDeleteAttribute",
            "HttpPatchAttribute",
            "HttpOptionsAttribute",
            "AcceptVerbsAttribute");

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Read path calls a write",
        messageFormat:
            "'{0}' answers GET (or renders a view component) and calls '{1}', which writes. " +
            "Move the write behind a state-changing action ([HttpPost]) or into the service " +
            "flow that owns the change; a read path may change how a fact looks, never what it is.",
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Browsers prefetch links, crawlers follow them, and a GET carries no antiforgery token. " +
            "A write reached from a GET action or a view component runs whenever anyone loads the page. " +
            "A write here is a member of an I*Write interface or a method marked [ExternalWrite].");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        // A missing attribute only switches off the [ExternalWrite] half; the
        // I*Write half needs no symbol.
        var externalWrite = context.Compilation.GetTypeByMetadataName(ExternalWriteAttributeFullName);

        context.RegisterOperationAction(
            ctx => AnalyzeInvocation(ctx, externalWrite),
            OperationKind.Invocation);
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, INamedTypeSymbol? externalWrite)
    {
        var op = (IInvocationOperation)context.Operation;

        var method = context.ContainingSymbol.EnclosingOrdinaryMethod();
        if (method is null || !IsReadPath(method))
            return;

        var target = op.TargetMethod;
        if (!IsWriteInterfaceMember(target) &&
            !(externalWrite is not null && ExternalWriteCheck.IsExternalWrite(target, externalWrite)))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            op.Syntax.GetLocation(),
            $"{method.ContainingType.Name}.{method.Name}",
            $"{target.ContainingType.Name}.{target.Name}"));
    }

    private static bool IsReadPath(IMethodSymbol method)
    {
        var type = method.ContainingType;

        if (type.InheritsFromOrEquals(ViewComponentFullName))
            return method.Name is "Invoke" or "InvokeAsync";

        if (!type.InheritsFromOrEquals(ControllerBaseFullName) ||
            method.DeclaredAccessibility != Accessibility.Public ||
            method.IsStatic)
        {
            return false;
        }

        var hasVerb = false;
        foreach (var attr in method.GetAttributes())
        {
            var attributeClass = attr.AttributeClass;
            if (attributeClass is null ||
                !string.Equals(attributeClass.ContainingNamespace?.ToDisplayString(), MvcNamespace, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(attributeClass.Name, "NonActionAttribute", StringComparison.Ordinal))
                return false;

            if (ReadVerbAttributes.Contains(attributeClass.Name) ||
                (string.Equals(attributeClass.Name, "AcceptVerbsAttribute", StringComparison.Ordinal) &&
                 AcceptsReadVerb(attr)))
            {
                return true;
            }

            hasVerb |= VerbAttributes.Contains(attributeClass.Name);
        }

        return !hasVerb;
    }

    /// <summary>
    /// True when an <c>[AcceptVerbs]</c> lists GET or HEAD, whether passed as one
    /// string or through its <c>params string[]</c> constructor.
    /// </summary>
    private static bool AcceptsReadVerb(AttributeData attr)
    {
        foreach (var arg in attr.ConstructorArguments)
        {
            if (arg.Kind == TypedConstantKind.Array)
            {
                foreach (var item in arg.Values)
                {
                    if (IsReadVerb(item.Value as string))
                        return true;
                }
            }
            else if (IsReadVerb(arg.Value as string))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReadVerb(string? verb) =>
        string.Equals(verb, "GET", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(verb, "HEAD", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the target is declared on — or, for a call through a concrete
    /// class, implements a member of — a Humans <c>I*Write</c> interface.
    /// </summary>
    private static bool IsWriteInterfaceMember(IMethodSymbol method)
    {
        if (IsWriteInterface(method.ContainingType))
            return true;

        foreach (var iface in method.ContainingType.AllInterfaces)
        {
            if (!IsWriteInterface(iface))
                continue;

            foreach (var member in iface.GetMembers(method.Name))
            {
                if (member is IMethodSymbol ifaceMethod &&
                    SymbolEqualityComparer.Default.Equals(
                        method.ContainingType.FindImplementationForInterfaceMember(ifaceMethod), method))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsWriteInterface(INamedTypeSymbol? type) =>
        type is { TypeKind: TypeKind.Interface } &&
        type.Name.Length > WriteInterfaceSuffix.Length + 1 &&
        type.Name[0] == 'I' &&
        type.Name.EndsWith(WriteInterfaceSuffix, StringComparison.Ordinal) &&
        type.ContainingNamespace.ToDisplayString().StartsWith(HumansNamespacePrefix, StringComparison.Ordinal);
}
