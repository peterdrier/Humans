using System.Runtime.CompilerServices;
using Humans.Domain.Attributes;

// The analyzer seam (design §10): Humans.Analyzers' AssemblyScope keys section
// assemblies off this marker rather than off the three literal assembly names, so a
// section that moves out of Humans.Application/Web/Infrastructure keeps all 27 rules.
// It is also what MVC's SectionControllerFeatureProvider keys off, so the internal
// ShiftsController / ShiftAdminController / ShiftDashboardController /
// ShiftWorkloadAdminController / VolunteerTrackingController / ShiftProfileController
// are still discovered and routed
// (memory/architecture/section-controllers-need-feature-provider.md), and what
// SectionViewComponentFeatureProvider keys off for the internal
// OnboardingShiftsListViewComponent / ShiftsGalleryViewComponent.
[assembly: Section("Shifts")]

// Castle DynamicProxy, behind NSubstitute in Humans.Shifts.Tests, needs to see the
// internal IShiftManagementService / IShiftManagementRepository / IShiftSignupService /
// IShiftRowView / IVolunteerTrackingService and friends to proxy them.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
