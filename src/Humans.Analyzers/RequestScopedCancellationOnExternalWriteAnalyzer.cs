using System;
using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Humans.Analyzers;

/// <summary>
/// HUM0033 — a state-changing controller action must not pass a request-scoped
/// cancellation token into a method marked <c>[ExternalWrite]</c>.
/// </summary>
/// <remarks>
/// <para>
/// Rule: <c>memory/architecture/cancellation-token-propagation.md</c>. A
/// request-scoped token (<c>HttpContext.RequestAborted</c>, or the action's own
/// <c>CancellationToken</c> parameter, which the framework binds to the same
/// thing) fires when the user closes the tab or navigates away. Handing it to an
/// outbound call that mutates a third-party system means an impatient admin can
/// tear a vendor write in half — the incident behind
/// nobodies-collective/Humans#950 was a Google Workspace reconcile abandoned
/// partway through applying group memberships and Drive permissions.
/// </para>
/// <para>
/// <b>Why the rule is shaped this way.</b> The obvious formulation — "a
/// request-scoped token must never reach an external-write client" — is not
/// decidable by a Roslyn analyzer: the taint crosses assemblies (Web →
/// Application interface → Infrastructure client) and Web's compilation only
/// sees the interface symbol, never the implementation that eventually calls
/// Google. So the check is made local on both ends: <c>[ExternalWrite]</c>
/// carries the "this reaches a remote mutation" fact across the assembly
/// boundary in metadata, and the HTTP verb of the enclosing action carries the
/// "this request is state-changing" fact. A <c>[HttpGet]</c> action is exempt by
/// design — read-only outbound fetches are the case where honouring the token is
/// correct.
/// </para>
/// <para>
/// <b>Known limits</b> (deliberate — under-detection is preferable to noise):
/// only direct call sites inside an action body are checked, so a token laundered
/// through a private controller helper is missed; and a method nobody has marked
/// <c>[ExternalWrite]</c> is not checked at all.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RequestScopedCancellationOnExternalWriteAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0033";

    public const string ExternalWriteAttributeFullName =
        "Humans.Application.Architecture.ExternalWriteAttribute";

    private const string MvcNamespace = "Microsoft.AspNetCore.Mvc";
    private const string RequestAbortedPropertyName = "RequestAborted";
    private const string CancellationTokenTypeName = "System.Threading.CancellationToken";

    private static readonly ImmutableHashSet<string> StateChangingVerbAttributes =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "HttpPostAttribute",
            "HttpPutAttribute",
            "HttpDeleteAttribute",
            "HttpPatchAttribute");

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "State-changing action passes a request-scoped cancellation token to an [ExternalWrite] method",
        messageFormat:
            "'{0}' is a state-changing action and passes a request-scoped cancellation token to " +
            "'{1}', which reaches a write to a third-party system. Pass CancellationToken.None " +
            "(or enqueue the work through Hangfire) — see " +
            "memory/architecture/cancellation-token-propagation.md.",
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "HttpContext.RequestAborted fires when the user closes the tab or navigates away. " +
            "A local DB write torn mid-flight rolls back; a remote write torn mid-flight leaves " +
            "the vendor in a state we have no record of and no compensation for. A vendor sync " +
            "must finish once started, regardless of whether the human who triggered it is still " +
            "watching.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        // Request-scoped tokens only exist in the Web assembly.
        if (context.Compilation.Assembly.Name is not AssemblyScope.Web)
            return;

        var externalWrite = context.Compilation.GetTypeByMetadataName(ExternalWriteAttributeFullName);
        if (externalWrite is null)
            return;

        var grandfathered = GrandfatheredCheck.Resolve(context.Compilation);

        context.RegisterOperationAction(
            ctx => AnalyzeInvocation(ctx, externalWrite, grandfathered),
            OperationKind.Invocation);
    }

    private static void AnalyzeInvocation(
        OperationAnalysisContext context,
        INamedTypeSymbol externalWriteAttribute,
        INamedTypeSymbol? grandfatheredAttribute)
    {
        var op = (IInvocationOperation)context.Operation;
        if (!IsExternalWrite(op.TargetMethod, externalWriteAttribute))
            return;

        var action = EnclosingMethod(context.ContainingSymbol);
        if (action is null || !IsStateChangingAction(action))
            return;

        foreach (var argument in op.Arguments)
        {
            if (!IsRequestScopedToken(argument.Value, action))
                continue;

            context.ReportDiagnostic(Diagnostic.Create(
                descriptor: Rule,
                location: argument.Value.Syntax.GetLocation(),
                effectiveSeverity: GrandfatheredCheck.EffectiveSeverity(
                    action, grandfatheredAttribute, DiagnosticId),
                additionalLocations: null,
                properties: null,
                messageArgs: [action.Name, op.TargetMethod.Name]));
            return;
        }
    }

    /// <summary>
    /// True when the invoked method — or, for a call made through a concrete
    /// class, an interface member it implements — carries <c>[ExternalWrite]</c>.
    /// </summary>
    private static bool IsExternalWrite(IMethodSymbol method, INamedTypeSymbol attribute)
    {
        if (HasAttribute(method, attribute))
            return true;

        foreach (var iface in method.ContainingType.AllInterfaces)
        {
            foreach (var member in iface.GetMembers(method.Name))
            {
                if (member is not IMethodSymbol ifaceMethod)
                    continue;

                var impl = method.ContainingType.FindImplementationForInterfaceMember(ifaceMethod);
                if (SymbolEqualityComparer.Default.Equals(impl, method) &&
                    HasAttribute(ifaceMethod, attribute))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasAttribute(ISymbol symbol, INamedTypeSymbol attribute)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attribute))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The ordinary method containing the operation. Walks out of lambdas and
    /// local functions so a call inside <c>await Task.Run(() =&gt; …)</c> is still
    /// attributed to the action.
    /// </summary>
    private static IMethodSymbol? EnclosingMethod(ISymbol? symbol)
    {
        for (var current = symbol; current is not null; current = current.ContainingSymbol)
        {
            if (current is IMethodSymbol { MethodKind: MethodKind.Ordinary } method)
                return method;
        }

        return null;
    }

    private static bool IsStateChangingAction(IMethodSymbol method)
    {
        foreach (var attr in method.GetAttributes())
        {
            var attributeClass = attr.AttributeClass;
            if (attributeClass is null)
                continue;

            if (StateChangingVerbAttributes.Contains(attributeClass.Name) &&
                string.Equals(
                    attributeClass.ContainingNamespace?.ToDisplayString(),
                    MvcNamespace,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A request-scoped token is either <c>…RequestAborted</c> (whatever it is
    /// reached through — <c>HttpContext</c>, <c>ControllerContext.HttpContext</c>,
    /// <c>Request.HttpContext</c>) or the enclosing action's own
    /// <c>CancellationToken</c> parameter, which MVC model-binds to exactly that.
    /// </summary>
    private static bool IsRequestScopedToken(IOperation value, IMethodSymbol action)
    {
        var unwrapped = Unwrap(value);

        return unwrapped switch
        {
            IPropertyReferenceOperation property =>
                string.Equals(property.Property.Name, RequestAbortedPropertyName, StringComparison.Ordinal),
            IParameterReferenceOperation parameter =>
                string.Equals(
                    parameter.Parameter.Type.ToDisplayString(),
                    CancellationTokenTypeName,
                    StringComparison.Ordinal) &&
                SymbolEqualityComparer.Default.Equals(parameter.Parameter.ContainingSymbol, action),
            _ => false
        };
    }

    private static IOperation Unwrap(IOperation operation)
    {
        var current = operation;
        while (true)
        {
            switch (current)
            {
                case IConversionOperation conversion:
                    current = conversion.Operand;
                    break;
                case IParenthesizedOperation parenthesized:
                    current = parenthesized.Operand;
                    break;
                default:
                    return current;
            }
        }
    }
}
