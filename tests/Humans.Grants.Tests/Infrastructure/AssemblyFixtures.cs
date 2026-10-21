using Humans.Grants.Tests.Infrastructure;
using Xunit;

// One EF model build for the whole assembly instead of paying for it inside whichever
// [HumansFact]/[HumansTheory] happens to run first.
[assembly: AssemblyFixture(typeof(GrantsModelWarmupFixture))]
