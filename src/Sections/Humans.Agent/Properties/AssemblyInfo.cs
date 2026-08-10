using System.Runtime.CompilerServices;
using Humans.Domain.Attributes;

// The analyzer seam (design §10): Humans.Analyzers' AssemblyScope keys section assemblies
// off this marker rather than off the three literal assembly names, so a section that moves
// out of Humans.Application/Web/Infrastructure keeps all 27 rules. It is also what MVC's
// SectionControllerFeatureProvider keys off, so the internal AgentController /
// AdminAgentController / AgentApiController are still discovered and routed
// (memory/architecture/section-controllers-need-feature-provider.md).
//
// "Agent" is the section key AgentSectionDocReader's src/Sections/Humans.{key}/Docs probe
// uses, and it matches Docs/Agent.md.
[assembly: Section("Agent")]

// Castle DynamicProxy, behind NSubstitute in Humans.Agent.Tests, needs to see the internal
// section interfaces to proxy them.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
