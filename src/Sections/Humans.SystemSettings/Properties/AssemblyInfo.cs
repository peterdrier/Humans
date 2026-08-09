using System.Runtime.CompilerServices;
using Humans.Domain.Attributes;

// The analyzer seam (design §10): Humans.Analyzers' AssemblyScope keys section
// assemblies off this marker rather than off the three literal assembly names, so a
// section that moves out of Humans.Application/Web/Infrastructure keeps all 27 rules.
// It also carries the section name HUM0017/HUM0018 used to read from a per-type
// [Section("SystemSettings")] on the repository interface.
[assembly: Section("SystemSettings")]

// Castle DynamicProxy, behind NSubstitute in Humans.SystemSettings.Tests, needs to see
// ISystemSettingsRepository to proxy it.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
