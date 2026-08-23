using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Settings.Tests, needs to see
// ISettingsRepository to proxy it.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
