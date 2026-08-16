using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in the section's tests, needs to see the
// internal IUserService / IUserRepository / IProfilePictureService and friends to proxy
// them.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
