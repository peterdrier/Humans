using Humans.Domain.Attributes;

// The analyzer seam (design §10): Humans.Analyzers' AssemblyScope keys section assemblies
// off this marker rather than off the three literal assembly names, so a section that moves
// out of Humans.Application/Web/Infrastructure keeps all 27 rules. It is also what MVC's
// SectionControllerFeatureProvider keys off, so the internal DevLoginController and
// DevSeedController are still discovered and routed
// (memory/architecture/section-controllers-need-feature-provider.md).
//
// "Development" is deliberately NOT an AgentSectionKeys entry — the whitelist is user-facing
// sections, and this one is dev tooling that 404s outside Development/preview. The doc still
// sits at src/Sections/Humans.Development/Docs/Development.md so AgentSectionDocReader's
// src/Sections/Humans.{key}/Docs/{key}.md fallback would satisfy it if the key were ever added.
//
// InternalsVisibleTo("DynamicProxyGenAssembly2") is declared in the .csproj rather than here:
// DevPersonaSeederTests substitutes a dozen interfaces plus UserManager<User>.
[assembly: Section("Development")]
