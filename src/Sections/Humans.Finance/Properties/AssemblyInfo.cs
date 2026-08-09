using System.Runtime.CompilerServices;
using Humans.Domain.Attributes;

// The analyzer seam (design §10): Humans.Analyzers' AssemblyScope keys section
// assemblies off this marker rather than off the three literal assembly names, so a
// section that moves out of Humans.Application/Web/Infrastructure keeps all 27 rules.
// It also carries the section name HUM0017/HUM0018 used to read from a per-type
// [Section("Finance")] on the repository interface.
//
// "Finance", not "Holded": Holded is the external API client that stays in Base, with its
// own docs/sections/Holded.md. The section named here owns the holded_* tables and the
// FinanceDbContext, which names the live __EFMigrationsHistory_Finance table and must not
// be renamed. The table-name/section-name mismatch is recorded on
// nobodies-collective/Humans#1012 and deferred there (design §15 step 10).
[assembly: Section("Finance")]

// Castle DynamicProxy, behind NSubstitute in Humans.Finance.Tests, needs to see
// IHoldedRepository to proxy it.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
