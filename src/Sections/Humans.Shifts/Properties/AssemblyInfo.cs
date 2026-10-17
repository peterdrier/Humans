using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Shifts.Tests, needs to see the
// internal IShiftManagement / IShiftManagementRepository / IShiftSignupService /
// IShiftRowView / IVolunteerTrackingManagement and friends to proxy them.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
