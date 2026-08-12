using System.Runtime.CompilerServices;
using Humans.Domain.Attributes;

// The analyzer seam (design §10): Humans.Analyzers' AssemblyScope keys section assemblies
// off this marker rather than off the three literal assembly names, so a section that moves
// out of Humans.Application/Web/Infrastructure keeps all 27 rules. It is also what MVC's
// SectionControllerFeatureProvider keys off, so the internal ConsentController,
// LegalController and AdminLegalDocumentsController are still discovered and routed
// (memory/architecture/section-controllers-need-feature-provider.md).
[assembly: Section("Consent")]

// Castle DynamicProxy, behind NSubstitute in Humans.Consent.Tests, needs to see the
// internal section types to proxy them.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
