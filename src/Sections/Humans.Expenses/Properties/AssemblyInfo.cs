using System.Runtime.CompilerServices;
using Humans.Domain.Attributes;

// The analyzer seam (design §10): Humans.Analyzers' AssemblyScope keys section
// assemblies off this marker rather than off the three literal assembly names, so a
// section that moves out of Humans.Application/Web/Infrastructure keeps all 27 rules.
// It is also what MVC's SectionControllerFeatureProvider keys off, so the internal
// ExpensesController is still discovered and routed
// (memory/architecture/section-controllers-need-feature-provider.md).
//
// The section owns holded_expense_outbox_events despite the prefix: the outbox is Expenses'
// own record of what it still owes Holded, and its EF configuration has always lived in
// Configurations/Expenses/. holded_expense_docs is Finance's and stayed there in A2
// (memory/architecture/vendor-connectors-own-sections.md).
[assembly: Section("Expenses")]

// Castle DynamicProxy, behind NSubstitute in Humans.Expenses.Tests, needs to see the
// internal IExpenseRepository to proxy it.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
