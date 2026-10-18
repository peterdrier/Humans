using System.Runtime.CompilerServices;
using Humans.Users.Services;

// Castle DynamicProxy, behind NSubstitute in the section's tests, needs to see the
// internal IUserManagement / IUserRepository / IProfilePictureService and friends to proxy
// them.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
