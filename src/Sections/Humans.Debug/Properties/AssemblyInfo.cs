using Humans.Domain.Attributes;

// The analyzer seam (design §10): Humans.Analyzers' AssemblyScope keys section assemblies
// off this marker rather than off the three literal assembly names, so a section that moves
// out of Humans.Application/Web/Infrastructure keeps all 27 rules. It is also what MVC's
// SectionControllerFeatureProvider keys off, so the internal DebugController is still
// discovered and routed
// (memory/architecture/section-controllers-need-feature-provider.md).
//
// No InternalsVisibleTo("DynamicProxyGenAssembly2"): nothing in Humans.Debug.Tests
// substitutes a section type — the section is one controller over Base diagnostics
// singletons, and its only unit test covers the reflection-built format gallery.
[assembly: Section("Debug")]
