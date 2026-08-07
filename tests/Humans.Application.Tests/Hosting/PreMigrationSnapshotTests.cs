using AwesomeAssertions;
using Humans.Infrastructure.Hosting;

namespace Humans.Application.Tests.Hosting;

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
        PreMigrationSnapshot.FindUnfinishedSnapshot(_directory, Database).Should().BeNull();
    }

    [HumansFact]
    public void Completed_deploys_are_not_carried_forward()
    {
        Snapshot("humans-20260805T120000Z.dump");

        PreMigrationSnapshot.FindUnfinishedSnapshot(_directory, Database).Should().BeNull();
    }

    [HumansFact]
    public void Unfinished_snapshot_survives_the_boot_that_took_it()
    {
        var unfinished = Snapshot("humans-20260805T120000Z.dump" + PreMigrationSnapshot.UnfinishedSuffix);

        PreMigrationSnapshot.FindUnfinishedSnapshot(_directory, Database).Should().Be(unfinished);
    }

    /// <summary>
    /// The crash-loop case: restart two carries forward restart one's file instead of dumping a
    /// database whose schema the failed migration has already part-changed.
    /// </summary>
    [HumansFact]
    public void Earliest_unfinished_snapshot_wins()
    {
        var first = Snapshot("humans-20260805T120000Z.dump" + PreMigrationSnapshot.UnfinishedSuffix);
        Snapshot("humans-20260805T130000Z.dump" + PreMigrationSnapshot.UnfinishedSuffix);

        PreMigrationSnapshot.FindUnfinishedSnapshot(_directory, Database).Should().Be(first);
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

        PreMigrationSnapshot.FindUnfinishedSnapshot(_directory, Database).Should().BeNull();
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

        PreMigrationSnapshot.FindUnfinishedSnapshot(_directory, Database).Should().BeNull();
    }

    [HumansFact]
    public void Completing_the_migrations_retains_the_snapshot_and_ends_the_carry_forward()
    {
        Snapshot("humans-20260805T120000Z.dump" + PreMigrationSnapshot.UnfinishedSuffix);

        var promoted = PreMigrationSnapshot.PromoteUnfinishedSnapshots(_directory, Database);

        promoted.Should().ContainSingle()
            .Which.Should().Be(Path.Combine(_directory, "humans-20260805T120000Z.dump"));
        File.Exists(promoted[0]).Should().BeTrue();
        PreMigrationSnapshot.FindUnfinishedSnapshot(_directory, Database).Should().BeNull();
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

        PreMigrationSnapshot.FindUnfinishedSnapshot(_directory, Database).Should().BeNull();
        PreMigrationSnapshot.FindUnfinishedSnapshot(_directory, "humans_pr_42").Should().NotBeNull();
    }

    /// <summary>
    /// The gap nobodies-collective/Humans#989 closes: a genuine crash-loop retry, where the
    /// deploy that took the marker still has not finished, keeps carrying its snapshot forward.
    /// </summary>
    [HumansFact]
    public void A_marker_with_migrations_still_pending_is_not_stale()
    {
        var unfinished = Snapshot("humans-20260805T120000Z.dump" + PreMigrationSnapshot.UnfinishedSuffix);
        PreMigrationSnapshot.WriteFrontier(unfinished, ["HumansDbContext:20260805100000_AddFoo"]);

        PreMigrationSnapshot.FrontierStillPending(
            unfinished, ["HumansDbContext:20260805100000_AddFoo"]).Should().BeTrue();
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
            ["HumansDbContext:20260805100000_AddFoo", "HumansDbContext:20260805100001_AddBar"]);

        PreMigrationSnapshot.FrontierStillPending(
            unfinished, ["HumansDbContext:20260805100001_AddBar"]).Should().BeTrue();
    }

    /// <summary>
    /// The actual fix: when every migration recorded on the marker has since been applied, the
    /// deploy that took it finished, so it is stale rather than a rollback point worth reusing.
    /// </summary>
    [HumansFact]
    public void A_marker_whose_migrations_have_all_been_applied_is_stale()
    {
        var unfinished = Snapshot("humans-20260805T120000Z.dump" + PreMigrationSnapshot.UnfinishedSuffix);
        PreMigrationSnapshot.WriteFrontier(unfinished, ["HumansDbContext:20260805100000_AddFoo"]);

        PreMigrationSnapshot.FrontierStillPending(
            unfinished, ["HumansDbContext:20260805200000_AddLaterMigration"]).Should().BeFalse();
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

        PreMigrationSnapshot.FrontierStillPending(unfinished, []).Should().BeTrue();
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
        PreMigrationSnapshot.WriteFrontier(unfinished, ["HumansDbContext:20260805100000_AddFoo"]);

        PreMigrationSnapshot.PromoteUnfinishedSnapshots(_directory, Database);

        File.Exists(unfinished + ".migrations").Should().BeFalse();
    }

    private string Snapshot(string fileName)
    {
        var path = Path.Combine(_directory, fileName);
        File.WriteAllText(path, "PGDMP");
        return path;
    }
}
