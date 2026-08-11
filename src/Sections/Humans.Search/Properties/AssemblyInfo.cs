using System.Runtime.CompilerServices;
using Humans.Domain.Attributes;

// The analyzer seam (design §10): Humans.Analyzers' AssemblyScope keys section assemblies
// off this marker rather than off the three literal assembly names, so a section that moves
// out of Humans.Application/Web keeps all 27 rules — HUM0026/HUM0027, the orchestrator role
// pair this section is defined by, included. It is also what MVC's
// SectionControllerFeatureProvider keys off, so the internal SearchController is still
// discovered and routed (memory/architecture/section-controllers-need-feature-provider.md).
[assembly: Section("Search")]

// Castle DynamicProxy, behind NSubstitute in Humans.Search.Tests, needs to see the internal
// section types to proxy them (ISearchService).
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
