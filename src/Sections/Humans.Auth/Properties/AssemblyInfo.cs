using System.Runtime.CompilerServices;
using Humans.Domain.Attributes;

// The analyzer seam (design §10): Humans.Analyzers' AssemblyScope keys section
// assemblies off this marker rather than off the three literal assembly names, so a
// section that moves out of Humans.Application/Web/Infrastructure keeps all 27 rules.
[assembly: Section("Auth")]

// Castle DynamicProxy, behind NSubstitute in Humans.Auth.Tests, needs to see the
// internal IRoleAssignmentRepository to proxy it.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
