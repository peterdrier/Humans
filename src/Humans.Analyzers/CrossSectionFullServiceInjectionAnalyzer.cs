using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Humans.Analyzers;

/// <summary>
/// HUM0032 — cross-section calls go through <c>I&lt;Section&gt;ServiceRead</c>
/// when it suffices (peters-hard-rules: "Calls between sections … must be via
/// the I&lt;section&gt;ServiceRead when available"). A class under
/// <c>Humans.Application.Services.{A}</c> that injects another section's full
/// service interface (one extending a <c>*ServiceRead</c> base) but only ever
/// touches members the read interface already exposes must inject the read
/// interface instead.
/// <list type="bullet">
/// <item>Calling any member declared on the full interface (a write method)
/// keeps the injection legal — legitimate cross-section writers (orchestrators
/// etc.) are out of scope by construction.</item>
/// <item>Passing the dependency onward (as an argument, into a local, …)
/// disqualifies the type from analysis rather than flagging it — conservative,
/// no false positives.</item>
/// <item>Pre-existing violators carry class-level
/// <c>[Grandfathered("HUM0032", …)]</c> → Warning; new ones are Errors.</item>
/// </list>
/// </summary>
/// <remarks>
/// Runs in <c>Humans.Application</c> only. Caller section comes from the
/// <c>Services.{Section}</c> namespace, dependency section from the
/// <c>Interfaces.{Section}</c> namespace, both folded via
/// <see cref="Sections.Fold"/> (Users/Profile/Profiles are one section).
/// Every future read-split automatically arms this rule for its section.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CrossSectionFullServiceInjectionAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0032";

    private const string ReadInterfaceSuffix = "ServiceRead";

    private static readonly LocalizableString Title =
        "Cross-section caller only uses the read surface — inject the I*ServiceRead interface";

    private static readonly LocalizableString MessageFormat =
        "'{0}' (section '{1}') injects '{2}' (section '{3}') but only uses members of '{4}'. " +
        "Inject '{4}' instead — cross-section calls go through the read interface when it suffices. " +
        "See memory/architecture/section-read-write-split.md.";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "The I*ServiceRead interface is the contract for cross-section reads; holding the full " +
            "service interface grants write access the caller doesn't use and couples it to the owning " +
            "section's full surface. Demote the constructor parameter to the read interface. Callers " +
            "that genuinely write through the full interface are not flagged. Pre-existing violators " +
            "carry [Grandfathered(\"HUM0032\", …)] which downgrades to Warning until refactored.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (!string.Equals(context.Compilation.Assembly.Name, AssemblyScope.Application, System.StringComparison.Ordinal))
            return;

        var grandfatheredAttr = GrandfatheredCheck.Resolve(context.Compilation);

        context.RegisterSymbolStartAction(
            ctx => OnSymbolStart(ctx, grandfatheredAttr),
            SymbolKind.NamedType);
    }

    /// <summary>
    /// Per-dependency tracking state. <c>Holders</c> are the symbols through
    /// which the dependency is reachable (constructor parameters plus the
    /// fields/properties it is wired into). A reference to a holder that is
    /// not a direct member access (and not the wiring assignment itself)
    /// marks the dependency <c>Escaped</c>.
    /// </summary>
    private sealed class Candidate(
        INamedTypeSymbol fullInterface,
        string dependencySection,
        ImmutableArray<INamedTypeSymbol> readBases,
        ImmutableHashSet<ISymbol> holders,
        Location location)
    {
        public INamedTypeSymbol FullInterface { get; } = fullInterface;
        public string DependencySection { get; } = dependencySection;
        public ImmutableArray<INamedTypeSymbol> ReadBases { get; } = readBases;
        public ImmutableHashSet<ISymbol> Holders { get; } = holders;
        public Location Location { get; } = location;
        public ConcurrentBag<ISymbol> UsedMembers { get; } = [];
        public int Escaped;
    }

    private enum ReferenceKind
    {
        MemberAccess,
        Wiring,
        Escape,
    }

    private static void OnSymbolStart(SymbolStartAnalysisContext context, INamedTypeSymbol? grandfatheredAttr)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Class || type.IsAbstract)
            return;

        var callerSection = Sections.FromNamespace(type, Sections.ServiceNamespacePrefix);
        if (callerSection is null)
            return;

        var candidates = BuildCandidates(type, callerSection);
        if (candidates.Count == 0)
            return;

        // holder symbol → candidate, for O(1) classification of references.
        var byHolder = new Dictionary<ISymbol, Candidate>(SymbolEqualityComparer.Default);
        foreach (var candidate in candidates)
        {
            foreach (var holder in candidate.Holders)
                byHolder[holder] = candidate;
        }

        context.RegisterOperationAction(
            ctx => ClassifyReference(ctx.Operation, byHolder),
            OperationKind.ParameterReference,
            OperationKind.FieldReference,
            OperationKind.PropertyReference);

        context.RegisterSymbolEndAction(ctx => Report(ctx, type, callerSection, candidates, grandfatheredAttr));
    }

    private static List<Candidate> BuildCandidates(INamedTypeSymbol type, string callerSection)
    {
        var candidates = new List<Candidate>();

        foreach (var ctor in type.InstanceConstructors)
        {
            foreach (var parameter in ctor.Parameters)
            {
                if (parameter.Type is not INamedTypeSymbol { TypeKind: TypeKind.Interface } fullInterface)
                    continue;

                var readBases = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
                foreach (var baseInterface in fullInterface.AllInterfaces)
                {
                    if (baseInterface.Name.EndsWith(ReadInterfaceSuffix, System.StringComparison.Ordinal))
                        readBases.Add(baseInterface);
                }
                if (readBases.Count == 0)
                    continue;

                var dependencySection = Sections.FromNamespace(fullInterface, Sections.InterfaceNamespacePrefix);
                if (dependencySection is null
                    || string.Equals(dependencySection, callerSection, System.StringComparison.Ordinal))
                    continue;

                var holders = ImmutableHashSet.CreateBuilder<ISymbol>(SymbolEqualityComparer.Default);
                holders.Add(parameter);
                foreach (var member in type.GetMembers())
                {
                    switch (member)
                    {
                        case IFieldSymbol { IsStatic: false } field
                            when SymbolEqualityComparer.Default.Equals(field.Type, fullInterface):
                            holders.Add(field);
                            break;
                        case IPropertySymbol { IsStatic: false } property
                            when SymbolEqualityComparer.Default.Equals(property.Type, fullInterface):
                            holders.Add(property);
                            break;
                    }
                }

                candidates.Add(new Candidate(
                    fullInterface,
                    dependencySection,
                    readBases.ToImmutable(),
                    holders.ToImmutable(),
                    parameter.Locations.Length > 0 ? parameter.Locations[0] : ctor.Locations[0]));
            }
        }

        return candidates;
    }

    private static void ClassifyReference(IOperation reference, Dictionary<ISymbol, Candidate> byHolder)
    {
        var referencedSymbol = reference switch
        {
            IParameterReferenceOperation p => (ISymbol)p.Parameter,
            IFieldReferenceOperation f => f.Field,
            IPropertyReferenceOperation pr => pr.Property,
            _ => null,
        };

        if (referencedSymbol is null || !byHolder.TryGetValue(referencedSymbol, out var candidate))
            return;

        switch (Classify(reference, candidate, out var accessedMember))
        {
            case ReferenceKind.MemberAccess:
                candidate.UsedMembers.Add(accessedMember!.OriginalDefinition);
                break;
            case ReferenceKind.Wiring:
                break;
            case ReferenceKind.Escape:
                System.Threading.Interlocked.Exchange(ref candidate.Escaped, 1);
                break;
        }
    }

    private static ReferenceKind Classify(IOperation reference, Candidate candidate, out ISymbol? accessedMember)
    {
        accessedMember = null;

        switch (reference.Parent)
        {
            case IInvocationOperation invocation when ReferenceEquals(invocation.Instance, reference):
                accessedMember = invocation.TargetMethod;
                return ReferenceKind.MemberAccess;

            case IPropertyReferenceOperation propertyAccess when ReferenceEquals(propertyAccess.Instance, reference):
                accessedMember = propertyAccess.Property;
                return ReferenceKind.MemberAccess;

            case IConditionalAccessOperation conditional when ReferenceEquals(conditional.Operation, reference):
                accessedMember = conditional.WhenNotNull switch
                {
                    IInvocationOperation inv when inv.Instance is IConditionalAccessInstanceOperation => inv.TargetMethod,
                    IPropertyReferenceOperation prop when prop.Instance is IConditionalAccessInstanceOperation => prop.Property,
                    _ => null,
                };
                return accessedMember is null ? ReferenceKind.Escape : ReferenceKind.MemberAccess;

            // Constructor wiring: `_teams = teams;`. The value side is wiring
            // when it flows into a holder; the target side (the holder being
            // written) is always wiring — the value gets its own classification.
            case ISimpleAssignmentOperation assignment
                when ReferenceEquals(assignment.Value, reference) && TargetIsHolder(assignment.Target, candidate):
            case ISimpleAssignmentOperation assignmentTarget
                when ReferenceEquals(assignmentTarget.Target, reference):
                return ReferenceKind.Wiring;

            // Null-guarded wiring: `_teams = teams ?? throw …`. The reference's
            // parent is the coalesce, not the assignment — classify the coalesce
            // node in the reference's place so the wiring (or member-access)
            // detection above still applies. Implicit conversions get the same
            // pass-through.
            case ICoalesceOperation coalesce when ReferenceEquals(coalesce.Value, reference):
            case IConversionOperation { IsImplicit: true }:
                return Classify(reference.Parent, candidate, out accessedMember);

            // `nameof(teams)` (e.g. in the ArgumentNullException guard) never
            // touches the object — benign, neither a use nor an escape.
            case INameOfOperation:
                return ReferenceKind.Wiring;

            default:
                return ReferenceKind.Escape;
        }
    }

    private static bool TargetIsHolder(IOperation target, Candidate candidate)
    {
        var symbol = target switch
        {
            IFieldReferenceOperation f => (ISymbol)f.Field,
            IPropertyReferenceOperation p => p.Property,
            _ => null,
        };
        return symbol is not null && candidate.Holders.Contains(symbol);
    }

    private static void Report(
        SymbolAnalysisContext context,
        INamedTypeSymbol type,
        string callerSection,
        List<Candidate> candidates,
        INamedTypeSymbol? grandfatheredAttr)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.Escaped != 0)
                continue;

            var usedMembers = new HashSet<ISymbol>(candidate.UsedMembers, SymbolEqualityComparer.Default);

            // An injected-but-unused dependency is dead-code territory, not a
            // demotion case — skip rather than mis-advise.
            if (usedMembers.Count == 0)
                continue;

            var readInterface = FindCoveringReadBase(candidate, usedMembers);
            if (readInterface is null)
                continue;

            var severity = GrandfatheredCheck.EffectiveSeverity(type, grandfatheredAttr, DiagnosticId);

            context.ReportDiagnostic(Diagnostic.Create(
                descriptor: Rule,
                location: candidate.Location,
                effectiveSeverity: severity,
                additionalLocations: null,
                properties: null,
                messageArgs:
                [
                    type.Name,
                    callerSection,
                    candidate.FullInterface.Name,
                    candidate.DependencySection,
                    readInterface.Name,
                ]));
        }
    }

    /// <summary>
    /// Returns the first read base interface that (with its own base
    /// interfaces) declares every used member, or null when none covers them
    /// all — i.e., the caller genuinely needs the full interface.
    /// </summary>
    private static INamedTypeSymbol? FindCoveringReadBase(Candidate candidate, HashSet<ISymbol> usedMembers)
    {
        foreach (var readBase in candidate.ReadBases)
        {
            var allowed = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default) { readBase };
            foreach (var inherited in readBase.AllInterfaces)
                allowed.Add(inherited);

            var covers = true;
            foreach (var member in usedMembers)
            {
                if (member.ContainingType is not INamedTypeSymbol containing || !allowed.Contains(containing))
                {
                    covers = false;
                    break;
                }
            }

            if (covers)
                return readBase;
        }

        return null;
    }
}
