using System.Runtime.CompilerServices;
using Humans.Domain.Attributes;

// The analyzer seam (design §10): Humans.Analyzers' AssemblyScope keys section
// assemblies off this marker rather than off the three literal assembly names, so a
// section that moves out of Humans.Application/Web/Infrastructure keeps all 27 rules.
// It also carries the section name HUM0017/HUM0018 used to read from a per-type
// [Section("Events")] on the repository interface.
//
// "Events", not "EventGuide": AgentSectionKeys, src/Sections/Humans.Events/Docs/Events.md and
// AgentSectionDocReader's src/Sections/Humans.{key}/Docs probe all key off it. The
// EventGuide name survives only on EventGuideDbContext, which names the live
// __EFMigrationsHistory_EventGuide table and must not be renamed.
[assembly: Section("Events")]

// Castle DynamicProxy, behind NSubstitute in Humans.Events.Tests, needs to see
// IEventRepository to proxy it.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
