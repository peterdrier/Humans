using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.AuditLog.Tests, needs to see the
// internal IAuditLogRepository to proxy it.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
