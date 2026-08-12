using Humans.Domain.Attributes;

// The analyzer seam (design §10): Humans.Analyzers' AssemblyScope keys section assemblies
// off this marker rather than off the three literal assembly names, so a section that moves
// out of Humans.Application/Web/Infrastructure keeps all 27 rules. It is also what
// SectionDiscoveryExtensions logs the section by, and what GdprExportDependencyInjectionTests'
// SectionType(...) helper searches — the section has no controller, so
// SectionControllerFeatureProvider has nothing of Gdpr's to discover.
//
// "Gdpr" is not an AgentSectionKeys entry — the whitelist is user-facing sections and the
// export is a button on /Profile rather than a section of its own — so
// AgentSectionDocReader's src/Sections/Humans.{key}/Docs/{key}.md probe never fires for
// Docs/Gdpr.md. Verified rather than assumed (Scanner's check); the project folder and the
// filename match the canonical key, so the fallback would satisfy it if the key were added.
[assembly: Section("Gdpr")]

// No InternalsVisibleTo("DynamicProxyGenAssembly2"): Humans.Gdpr.Tests substitutes only
// IUserDataContributor, which is public on the contracts leaf.
