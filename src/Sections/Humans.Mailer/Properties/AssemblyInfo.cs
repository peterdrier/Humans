using System.Runtime.CompilerServices;
using Humans.Domain.Attributes;

// The analyzer seam (design §10): Humans.Analyzers' AssemblyScope keys section assemblies
// off this marker rather than off the three literal assembly names, so a section that moves
// out of Humans.Application/Web/Infrastructure keeps all 27 rules. It is also what MVC's
// SectionControllerFeatureProvider keys off, so the internal MailerAdminController is still
// discovered and routed
// (memory/architecture/section-controllers-need-feature-provider.md).
//
// "Mailer" is deliberately NOT an AgentSectionKeys entry — the whitelist is user-facing and
// this section is operator-only, so AgentSectionDocReader's
// src/Sections/Humans.{key}/Docs/{key}.md probe never fires for Docs/Mailer.md. Verified
// rather than assumed (Scanner's check); the file is in the right place if the key is ever
// added.
[assembly: Section("Mailer")]

// Castle DynamicProxy, behind NSubstitute in Humans.Mailer.Tests, substitutes the section's
// own internal interfaces — IMailerLiteService, IMailerImportService,
// IMailerAudienceSyncService and IMailerAudience are all stubbed by the controller tests.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
