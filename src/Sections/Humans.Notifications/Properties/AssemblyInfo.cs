using System.Runtime.CompilerServices;
using Humans.Domain.Attributes;

// The analyzer seam (design §10): Humans.Analyzers' AssemblyScope keys section assemblies
// off this marker rather than off the three literal assembly names, so a section that moves
// out of Humans.Application/Web/Infrastructure keeps all 27 rules. It is also what MVC's
// SectionControllerFeatureProvider keys off, so the internal NotificationsController is
// still discovered and routed
// (memory/architecture/section-controllers-need-feature-provider.md) — and what
// SectionViewComponentFeatureProvider keys off, so the internal
// NotificationBellViewComponent is still discovered and invocable by name from Shell's
// layouts.
[assembly: Section("Notifications")]

// Castle DynamicProxy, behind NSubstitute in Humans.Notifications.Tests, needs to see the
// internal section types to proxy them.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
