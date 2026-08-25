using Humans.Integration.Tests.Infrastructure;
using Xunit;

// One Postgres container and one migration pass for the whole assembly
// (nobodies-collective/Humans#764). Before this, each test class owned an
// IClassFixture<HumansWebApplicationFactory>, so a run started 30 Testcontainers
// Postgres instances and ran the full migration chain in every one of them — the
// resource contention behind the timeout-shaped "pre-existing failures".
[assembly: AssemblyFixture(typeof(HumansTestDatabase))]

// Hosts are isolated at the database and service level, but Program.cs configures
// Serilog through the process-wide Log.Logger. Concurrent WebApplicationFactory
// startup/shutdown therefore lets one host replace or flush another host's logger,
// making startup-failure assertions nondeterministic. Keep this assembly sequential
// until logging is host-scoped; the Postgres container remains shared.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
