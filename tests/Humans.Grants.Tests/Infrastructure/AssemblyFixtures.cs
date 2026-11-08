using Humans.Grants.Tests.Infrastructure;
using Xunit;

// One EF model build for the whole assembly, not inside whichever test runs first.
[assembly: AssemblyFixture(typeof(GrantsModelWarmupFixture))]
