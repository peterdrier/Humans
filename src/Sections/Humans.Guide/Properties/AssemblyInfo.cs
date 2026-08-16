using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Guide.Tests, needs to see the internal
// interfaces the section substitutes (IGuideRenderer, ITeamServiceRead collaborators).
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
