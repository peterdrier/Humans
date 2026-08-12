using System.Runtime.CompilerServices;
using Humans.Domain.Attributes;

// The analyzer seam (design §10): Humans.Analyzers' AssemblyScope keys section assemblies
// off this marker rather than off the three literal assembly names, so a section that moves
// out of Humans.Application/Web/Infrastructure keeps all 27 rules. It is also what MVC's
// SectionControllerFeatureProvider keys off, so the internal CantinaController is still
// discovered and routed
// (memory/architecture/section-controllers-need-feature-provider.md), and what
// SectionResourceTypes() keys off to find CantinaResource.
[assembly: Section("Cantina")]

// Castle DynamicProxy, behind NSubstitute in Humans.Cantina.Tests, substitutes the
// cross-section read interfaces the roster service composes over.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
