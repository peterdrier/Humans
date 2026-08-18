using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Mailer.Tests, substitutes the section's
// own internal interfaces — IMailerLiteService, IMailerImportService,
// IMailerAudienceSyncService and IMailerAudience are all stubbed by the controller tests.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
