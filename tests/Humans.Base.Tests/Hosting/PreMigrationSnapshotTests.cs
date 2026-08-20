using AwesomeAssertions;
using Humans.Base.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Humans.Base.Tests.Hosting;

/// <summary>
/// Covers the part of <see cref="PreMigrationSnapshot"/> that decides whether a boot dumps at
/// all (nobodies-collective/Humans#845). Taking the dump needs a live database and a
/// <c>pg_dump</c> binary, but the rule that actually protects the rollback point is pure
/// filesystem bookkeeping: a failed migration crash-loops the container, so the snapshot has to
/// belong to the deploy rather than the boot or the second restart archives the damage over it.
/// </summary>
public sealed class PreMigrationSnapshotTests : IDisposable
{
    private const string Database = "humans";

    private readonly string _directory =
        Directory.CreateTempSubdirectory("pre-migration-snapshot-tests").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [HumansFact]
    public void No_snapshots_means_nothing_to_carry_forward()
    {
        PreMigrationSnapshot.UnfinishedSnapshots(_directory, Database).Should().BeEmpty();
    }

    [HumansFact]
    public void Completed_deploys_are_not_carried_forward()
    {
        Snapshot("humans-20260805T120000Z.dump");

        PreMigrationSnapshot.UnfinishedSnapshots(_directory, Database).Should().BeEmpty();
    }

    [HumansFact]
    public void Unfinished_snapshot_survives_the_boot_that_took_it()
    {
        var unfinished = Snapshot("humans-20260805T120000Z.dump" + PreMigrationSnapshot.UnfinishedSuffix);

        PreMigrationSnapshot.UnfinishedSnapshots(_directory, Database).Should().Equal(unfinished);
    }

    /// <summary>
    /// The crash-loop case: restart two carries forward restart one's file instead of dumping a
    /// database whose schema the failed migration has already part-changed. Oldest first, because
    /// among live markers the earliest is the one that predates the most.
    /// </summary>
    [HumansFact]
    public void Unfinished_snapshots_come_back_oldest_first()
    {
        var first = Snapshot("humans-20260805T120000Z.dump" + PreMigrationSnapshot.UnfinishedSuffix);
        var second = Snapshot("humans-20260805T130000Z.dump" + PreMigrationSnapshot.UnfinishedSuffix);

        PreMigrationSnapshot.UnfinishedSnapshots(_directory, Database).Should().Equal(first, second);
    }

    /// <summary>
    /// A dump only earns the suffix once <c>pg_dump</c> has exited successfully. The file it was
    /// being written into must never qualify: carrying a truncated dump forward would skip the
    /// next boot's dump and let it migrate on the strength of an unrestorable file.
    /// </summary>
    [HumansFact]
    public void A_dump_still_being_written_is_never_carried_forward()
    {
        Snapshot("humans-20260805T120000Z.dump.writing");

        PreMigrationSnapshot.UnfinishedSnapshots(_directory, Database).Should().BeEmpty();
    }

    [HumansFact]
    public void A_dump_still_being_written_is_never_promoted()
    {
        Snapshot("humans-20260805T120000Z.dump.writing");

        PreMigrationSnapshot.PromoteUnfinishedSnapshots(_directory, Database).Should().BeEmpty();
        File.Exists(Path.Combine(_directory, "humans-20260805T120000Z.dump")).Should().BeFalse();
    }

    [HumansFact]
    public void Another_databases_snapshot_is_never_carried_forward()
    {
        Snapshot("humans_pr_42-20260805T120000Z.dump" + PreMigrationSnapshot.UnfinishedSuffix);

        PreMigrationSnapshot.UnfinishedSnapshots(_directory, Database).Should().BeEmpty();
    }

    [HumansFact]
    public void Completing_the_migrations_retains_the_snapshot_and_ends_the_carry_forward()
    {
        Snapshot("humans-20260805T120000Z.dump" + PreMigrationSnapshot.UnfinishedSuffix);

        var promoted = PreMigrationSnapshot.PromoteUnfinishedSnapshots(_directory, Database);

        promoted.Should().ContainSingle()
            .Which.Should().Be(Path.Combine(_directory, "humans-20260805T120000Z.dump"));
        File.Exists(promoted[0]).Should().BeTrue();
        PreMigrationSnapshot.UnfinishedSnapshots(_directory, Database).Should().BeEmpty();
    }

    /// <summary>
    /// A boot with no pending migrations still completes, which is what clears the marker after
    /// an operator has recovered a failed deploy by restoring and rolling the image back.
    /// </summary>
    [HumansFact]
    public void Completing_the_migrations_clears_a_previous_deploys_marker()
    {
        Snapshot("humans-20260805T120000Z.dump" + PreMigrationSnapshot.UnfinishedSuffix);
        Snapshot("humans_pr_42-20260805T120000Z.dump" + PreMigrationSnapshot.UnfinishedSuffix);

        PreMigrationSnapshot.PromoteUnfinishedSnapshots(_directory, Database);

        PreMigrationSnapshot.UnfinishedSnapshots(_directory, Database).Should().BeEmpty();
        PreMigrationSnapshot.UnfinishedSnapshots(_directory, "humans_pr_42").Should().NotBeEmpty();
    }

    /// <summary>
    /// The gap nobodies-collective/Humans#989 closes: a genuine crash-loop retry, where the
    /// deploy that took the marker still has not finished, keeps carrying its snapshot forward.
    /// </summary>
    [HumansFact]
    public void A_marker_with_migrations_still_pending_is_not_stale()
    {
        var unfinished = Snapshot("humans-20260805T120000Z.dump" + PreMigrationSnapshot.UnfinishedSuffix);
        PreMigrationSnapshot.WriteFrontier(unfinished, ["UsersDbContext:20260805100000_AddFoo"]);

        PreMigrationSnapshot.FrontierStillPending(
            unfinished, ["UsersDbContext:20260805100000_AddFoo"], NullLogger.Instance).Should().BeTrue();
    }

    /// <summary>
    /// A partial application (migration 3 of 5 fails) must still carry forward: the test is "any
    /// recorded migration still pending", not "all".
    /// </summary>
    [HumansFact]
    public void A_marker_with_only_some_of_its_migrations_still_pending_is_not_stale()
    {
        var unfinished = Snapshot("humans-20260805T120000Z.dump" + PreMigrationSnapshot.UnfinishedSuffix);
        PreMigrationSnapshot.WriteFrontier(
            unfinished,
            ["UsersDbContext:20260805100000_AddFoo", "UsersDbContext:20260805100001_AddBar"]);

        PreMigrationSnapshot.FrontierStillPending(
            unfinished, ["UsersDbContext:20260805100001_AddBar"], NullLogger.Instance).Should().BeTrue();
    }

    /// <summary>
    /// The actual fix: when every migration recorded on the marker has since been applied, the
    /// deploy that took it finished, so it is stale rather than a rollback point worth reusing.
    /// </summary>
    [HumansFact]
    public void A_marker_whose_migrations_have_all_been_applied_is_stale()
    {
        var unfinished = Snapshot("humans-20260805T120000Z.dump" + PreMigrationSnapshot.UnfinishedSuffix);
        PreMigrationSnapshot.WriteFrontier(unfinished, ["UsersDbContext:20260805100000_AddFoo"]);

        PreMigrationSnapshot.FrontierStillPending(
            unfinished,
            ["UsersDbContext:20260805200000_AddLaterMigration"],
            NullLogger.Instance).Should().BeFalse();
    }

    /// <summary>
    /// A marker taken before nobodies-collective/Humans#989 shipped has no frontier sidecar.
    /// Unable to tell whether it is stale, it fails safe as the old unconditional carry-forward
    /// rather than risk losing a real rollback point.
    /// </summary>
    [HumansFact]
    public void A_marker_with_no_recorded_frontier_is_not_stale()
    {
        var unfinished = Snapshot("humans-20260805T120000Z.dump" + PreMigrationSnapshot.UnfinishedSuffix);

        PreMigrationSnapshot.FrontierStillPending(unfinished, [], NullLogger.Instance).Should().BeTrue();
    }

    /// <summary>
    /// The sidecar is published by rename, so the name readers look for never exists half-written:
    /// a write killed part-way through leaves only the temporary file, which nothing reads. A
    /// short frontier read as authoritative is the damaging case — it can omit the migration that
    /// is still pending and retire a marker that is this deploy's live rollback point.
    /// </summary>
    [HumansFact]
    public void A_frontier_write_killed_half_way_is_never_read_as_the_frontier()
    {
        var unfinished = Snapshot("humans-20260805T120000Z.dump" + PreMigrationSnapshot.UnfinishedSuffix);
        File.WriteAllText(unfinished + ".migrations.writing", "UsersDbContext:2026080510");

        PreMigrationSnapshot.FrontierStillPending(unfinished, [], NullLogger.Instance).Should().BeTrue();
        File.Exists(unfinished + ".migrations").Should().BeFalse();
    }

    [HumansFact]
    public void Publishing_a_frontier_leaves_no_temporary_file_behind()
    {
        var unfinished = Snapshot("humans-20260805T120000Z.dump" + PreMigrationSnapshot.UnfinishedSuffix);

        PreMigrationSnapshot.WriteFrontier(unfinished, ["UsersDbContext:20260805100000_AddFoo"]);

        File.Exists(unfinished + ".migrations.writing").Should().BeFalse();
        File.ReadAllLines(unfinished + ".migrations")
            .Should().Equal("UsersDbContext:20260805100000_AddFoo");
    }

    /// <summary>
    /// A sidecar the volume will not give up must not abort the boot — the one failure a deploy
    /// cannot recover from (<c>memory/architecture/no-startup-guards.md</c>). Unable to tell
    /// whether the marker is stale, it carries forward like a marker with no sidecar at all.
    /// </summary>
    [HumansFact]
    public void A_marker_whose_frontier_cannot_be_read_is_not_stale()
    {
        var unfinished = Snapshot("humans-20260805T120000Z.dump" + PreMigrationSnapshot.UnfinishedSuffix);
        PreMigrationSnapshot.WriteFrontier(unfinished, ["UsersDbContext:20260805100000_AddFoo"]);

        using var exclusive = new FileStream(
            unfinished + ".migrations", FileMode.Open, FileAccess.Read, FileShare.None);

        PreMigrationSnapshot.FrontierStillPending(
            unfinished,
            ["UsersDbContext:20260805200000_AddLaterMigration"],
            NullLogger.Instance).Should().BeTrue();
    }

    /// <summary>
    /// Promoting a marker drops its now-unneeded frontier sidecar along with the
    /// <see cref="PreMigrationSnapshot.UnfinishedSuffix"/> rename, so it does not linger as an
    /// orphan file next to the completed snapshot.
    /// </summary>
    [HumansFact]
    public void Promoting_a_marker_drops_its_frontier_sidecar()
    {
        var unfinished = Snapshot("humans-20260805T120000Z.dump" + PreMigrationSnapshot.UnfinishedSuffix);
        PreMigrationSnapshot.WriteFrontier(unfinished, ["UsersDbContext:20260805100000_AddFoo"]);

        PreMigrationSnapshot.PromoteUnfinishedSnapshots(_directory, Database);

        File.Exists(unfinished + ".migrations").Should().BeFalse();
    }

    /// <summary>
    /// A single live marker is what a crash-loop retry carries forward, and it is not retired.
    /// </summary>
    [HumansFact]
    public void A_live_marker_is_carried_forward()
    {
        var live = Snapshot("humans-20260805T120000Z.dump" + PreMigrationSnapshot.UnfinishedSuffix);
        PreMigrationSnapshot.WriteFrontier(live, ["UsersDbContext:20260805100000_AddFoo"]);

        Sut().CarryForwardMarker(_directory, Database, ["UsersDbContext:20260805100000_AddFoo"])
            .Should().Be(live);
        File.Exists(live).Should().BeTrue();
    }

    /// <summary>
    /// Every marker being stale is the ordinary "last deploy finished" case: all of them retire
    /// and the boot goes on to take its own dump.
    /// </summary>
    [HumansFact]
    public void All_markers_stale_means_this_boot_takes_its_own_dump()
    {
        var stale = Snapshot("humans-20260805T120000Z.dump" + PreMigrationSnapshot.UnfinishedSuffix);
        PreMigrationSnapshot.WriteFrontier(stale, ["UsersDbContext:20260805100000_AddFoo"]);

        Sut().CarryForwardMarker(_directory, Database, ["UsersDbContext:20260805200000_AddLater"])
            .Should().BeNull();
        File.Exists(stale).Should().BeFalse();
        File.Exists(Path.Combine(_directory, "humans-20260805T120000Z.dump")).Should().BeTrue();
    }

    /// <summary>
    /// A retirement that fails leaves its stale marker in front of the marker that is actually
    /// live, so judging the oldest marker alone would retire the stale one, see nothing else, and
    /// dump the half-migrated schema over the crash loop's real rollback point — which a later
    /// boot then promotes as the newest completed snapshot. The scan steps past the stale marker
    /// and finds the live one.
    /// </summary>
    [HumansFact]
    public void A_stale_marker_in_front_of_a_live_one_is_stepped_over()
    {
        var stale = Snapshot("humans-20260805T120000Z.dump" + PreMigrationSnapshot.UnfinishedSuffix);
        PreMigrationSnapshot.WriteFrontier(stale, ["UsersDbContext:20260805100000_AddFoo"]);
        var live = Snapshot("humans-20260805T130000Z.dump" + PreMigrationSnapshot.UnfinishedSuffix);
        PreMigrationSnapshot.WriteFrontier(live, ["UsersDbContext:20260805200000_AddBar"]);

        Sut().CarryForwardMarker(_directory, Database, ["UsersDbContext:20260805200000_AddBar"])
            .Should().Be(live);
        File.Exists(stale).Should().BeFalse();
        File.Exists(live).Should().BeTrue();
    }

    /// <summary>
    /// Connection string is never opened — <see cref="PreMigrationSnapshot"/> only parses it for
    /// the database name, and everything under test here is filesystem bookkeeping.
    /// </summary>
    private static PreMigrationSnapshot Sut() =>
        new($"Host=localhost;Database={Database};Username=humans", NullLogger.Instance);

    private string Snapshot(string fileName)
    {
        var path = Path.Combine(_directory, fileName);
        File.WriteAllText(path, "PGDMP");
        return path;
    }
}
