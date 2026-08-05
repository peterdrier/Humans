using Humans.Integration.Tests.Infrastructure;
using Xunit;

// One Postgres container, one app boot and one migration pass for the whole
// assembly (nobodies-collective/Humans#764). Before this, each test class owned
// an IClassFixture<HumansWebApplicationFactory>, so a run started 30 Testcontainers
// Postgres instances and ran the full migration chain in every one of them — the
// resource contention behind the timeout-shaped "pre-existing failures".
[assembly: AssemblyFixture(typeof(HumansWebApplicationFactory))]

// The assembly fixture is shared mutable state: one database, one set of
// Singleton caches and one set of NSubstitute stubs behind one app host. Test
// classes therefore must not run concurrently — IntegrationTestBase resets those
// stubs per test, and a sibling class running in parallel would observe the
// reset mid-assertion.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
