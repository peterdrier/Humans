using System.Runtime.CompilerServices;
using Humans.Domain.Attributes;

// The analyzer seam (design §10): Humans.Analyzers' AssemblyScope keys section assemblies off
// this marker, and HUM0017/HUM0018 read the section name from it.
//
// "Holded" is the section that owns the mirror tables (holded_ledger_lines, holded_accounts,
// holded_api_calls, holded_sync_states) and HoldedDbContext. The Holded *connector*
// (IHoldedClient) is a separate thing that stays in Base; Finance keeps the business meaning
// (category map, expense-doc matching, creditor bindings).
[assembly: Section("Holded")]

// Castle DynamicProxy, behind NSubstitute in Humans.Holded.Tests, needs to see
// IHoldedMirrorRepository to proxy it.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
