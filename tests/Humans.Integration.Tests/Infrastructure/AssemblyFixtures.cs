using Humans.Integration.Tests.Infrastructure;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

// One Postgres container and one migration pass for the whole assembly.
[assembly: AssemblyFixture(typeof(HumansTestDatabase))]

// Safe because each test has its own database, app host and stubs. The shared container's connection headroom is set
// in HumansTestDatabase.
[assembly: Parallelization(Mode = ParallelMode.Collections)]
