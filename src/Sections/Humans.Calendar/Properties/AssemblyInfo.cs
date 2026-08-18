using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Calendar.Tests, needs to see the
// internal section types to proxy them — ICalendarService and ICalendarRepository are both
// substituted there.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
