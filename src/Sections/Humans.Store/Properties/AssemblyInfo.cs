using System.Runtime.CompilerServices;
using Humans.Domain.Attributes;

// The analyzer seam (design §10): Humans.Analyzers' AssemblyScope keys section
// assemblies off this marker rather than off the three literal assembly names, so a
// section that moves out of Humans.Application/Web/Infrastructure keeps all 27 rules.
// It also carries the section name HUM0017/HUM0018 used to read from a per-type
// [Section("Store")] on the repository interface.
[assembly: Section("Store")]

// Castle DynamicProxy, behind NSubstitute in Humans.Store.Tests, needs to see
// IStoreRepository to proxy it. Internal visibility is the point of the section
// boundary, and Castle requires this grant for an internal type either way — an
// internal interface is no more proxyable than an internal class.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
