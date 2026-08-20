using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.MailerLite.Tests, substitutes the section's
// own internal interfaces — IMailerLiteService, IMailerLiteImportService,
// IMailerLiteAudienceSyncService and IMailerLiteAudience are all stubbed by the controller tests.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
