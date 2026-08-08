using System.Runtime.CompilerServices;
using Humans.Domain.Attributes;

// The analyzer seam (design §10): Humans.Analyzers' AssemblyScope keys section
// assemblies off this marker rather than off the three literal assembly names, so a
// section that moves out of Humans.Application/Web/Infrastructure keeps all 27 rules.
// It also carries the section name HUM0017/HUM0018 used to read from a per-type
// [Section("Store")] on the repository interface.
[assembly: Section("Store")]

// Castle DynamicProxy, behind NSubstitute in Humans.Store.Tests, proxies the internal
// Repository class. With IStoreRepository deleted (design §6a) there is no interface to
// substitute, so the class is the seam and the proxy generator needs to see it.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
