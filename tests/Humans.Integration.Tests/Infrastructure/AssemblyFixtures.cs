using Humans.Integration.Tests.Infrastructure;
using Xunit;

// One Postgres container and one migration pass for the whole assembly
// (nobodies-collective/Humans#764). Before this, each test class owned an
// IClassFixture<HumansWebApplicationFactory>, so a run started 30 Testcontainers
// Postgres instances and ran the full migration chain in every one of them — the
// resource contention behind the timeout-shaped "pre-existing failures".
[assembly: AssemblyFixture(typeof(HumansTestDatabase))]

// Parallelization is back on. It was disabled by nobodies-collective/Humans#764
// because the assembly shared one database, one set of Singleton caches and one set
// of NSubstitute stubs behind one app host; per-test isolation
// (nobodies-collective/Humans#983) means it shares none of those, so classes no
// longer observe each other. It also pays for the isolation: 2m39s sequential
// against 52s parallel, versus 33s for the shared-everything scheme it replaced.
//
// The one thing still shared is the Postgres container, and each concurrent app
// host holds a connection pool against it — see HumansTestDatabase for the
// max_connections headroom that assumes.
[assembly: CollectionBehavior(DisableTestParallelization = false)]
