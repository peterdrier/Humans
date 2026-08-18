using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Shifts.Tests, needs to see the
// internal IShiftManagementService / IShiftManagementRepository / IShiftSignupService /
// IShiftRowView / IVolunteerTrackingService and friends to proxy them.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
