using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.SystemSettings.Tests, needs to see
// ISystemSettingsRepository to proxy it.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
