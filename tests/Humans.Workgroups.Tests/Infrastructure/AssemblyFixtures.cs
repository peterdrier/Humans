using Humans.Workgroups.Tests.Infrastructure;
using Xunit;

// One EF model build for the whole assembly instead of paying for it inside whichever
// [HumansFact]/[HumansTheory] happens to run first — src/Sections/Humans.Workgroups/Docs/debt.yml WG-1.
[assembly: AssemblyFixture(typeof(WorkgroupsModelWarmupFixture))]
