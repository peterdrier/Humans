using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Guide.Tests, needs to see the internal
// interfaces the section substitutes (IGuideRenderer).
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
