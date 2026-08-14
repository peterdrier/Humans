using System.Runtime.CompilerServices;
using Humans.Domain.Attributes;

// The analyzer seam (design §10): Humans.Analyzers' AssemblyScope keys section
// assemblies off this marker rather than off the three literal assembly names, so a
// section that moves out of Humans.Application/Web/Infrastructure keeps all 27 rules.
// It is also what MVC's SectionControllerFeatureProvider keys off, so the section's
// controllers are still discovered and routed once lane 2 phase 3 moves them
// (memory/architecture/section-controllers-need-feature-provider.md).
[assembly: Section("Users")]

// Castle DynamicProxy, behind NSubstitute in the section's tests, needs to see the
// internal IUserService / IUserRepository / IProfilePictureService and friends to proxy
// them.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
