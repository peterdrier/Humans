using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Scanner.Tests, needs to see the
// internal controller to construct it against substituted cross-section read interfaces.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
