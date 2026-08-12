using System.Runtime.CompilerServices;
using Humans.Domain.Attributes;

// The analyzer seam (design §10): Humans.Analyzers' AssemblyScope keys section assemblies
// off this marker rather than off the three literal assembly names, so a section that moves
// out of Humans.Application/Web/Infrastructure keeps all 27 rules. It is also what MVC's
// SectionControllerFeatureProvider keys off, so the internal GuideController is still
// discovered and routed
// (memory/architecture/section-controllers-need-feature-provider.md).
[assembly: Section("Guide")]

// Castle DynamicProxy, behind NSubstitute in Humans.Guide.Tests, needs to see the internal
// interfaces the section substitutes (IGuideRenderer, ITeamServiceRead collaborators).
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
