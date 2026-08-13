using System.Runtime.CompilerServices;
using Humans.Domain.Attributes;

// The analyzer seam (design §10): Humans.Analyzers' AssemblyScope keys section assemblies
// off this marker rather than off the three literal assembly names, so a section that moves
// out of Humans.Application/Web/Infrastructure keeps all 27 rules. It is also what MVC's
// SectionControllerFeatureProvider and SectionViewComponentFeatureProvider key off, so the
// internal GoogleController and MyGoogleResourcesViewComponent are still discovered
// (memory/architecture/section-controllers-need-feature-provider.md).
[assembly: Section("GoogleIntegration")]

// Castle DynamicProxy, behind NSubstitute in Humans.GoogleIntegration.Tests, needs to see the
// internal services, repositories and connector abstractions to substitute them.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
