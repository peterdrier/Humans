namespace Humans.Base.Interfaces;

/// <summary>
/// The data migrations a section owns: operator-run copies from a legacy shape into the
/// section's own, listed and applied from <c>/Debug/DataMigrations</c> and reported by the
/// anonymous <c>/api/data-migrations</c> status endpoint.
/// </summary>
/// <remarks>
/// The review → confirm admin screen that memory/process/no-data-backfills.md requires, in
/// one place for every section. A migration is added by the commit that needs the copy,
/// applied by operators, and deleted by a retirement PR once production reports it done.
/// The delegates resolve the owning section's own services, so the copy stays inside the
/// owner and the host only calls this interface.
/// </remarks>
public interface ISectionDataMigrations : ISectionContribution
{
    /// <summary>The section's registered migrations.</summary>
    IEnumerable<DataMigration> Migrations();
}

/// <summary>One registered data migration.</summary>
/// <param name="Key">Stable, published id, section-first: <c>users.stub-profiles</c>.</param>
/// <param name="Section">The owning section's name.</param>
/// <param name="Title">What the copy does, for the operator.</param>
/// <param name="Retires">What the retirement PR deletes once the migration reports done.</param>
/// <param name="Status">Every row still pending, resolved from the request's services.</param>
/// <param name="Apply">Runs the copy for the acting admin; returns the rows written. Idempotent:
/// a second apply finds nothing pending.</param>
public sealed record DataMigration(
    string Key,
    string Section,
    string Title,
    string Retires,
    Func<IServiceProvider, CancellationToken, Task<DataMigrationStatus>> Status,
    Func<IServiceProvider, Guid, CancellationToken, Task<int>> Apply);

/// <summary>
/// What a migration has left to do: every pending row, with the values the copy will write,
/// so the operator sees each one before confirming. No sampling or paging — the dataset fits in RAM.
/// </summary>
/// <param name="Columns">Headings for each row's cells.</param>
/// <param name="PendingRows">One entry per pending row, cells in <paramref name="Columns"/> order.</param>
public sealed record DataMigrationStatus(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string>> PendingRows)
{
    /// <summary>How many rows are still pending.</summary>
    public int Pending => PendingRows.Count;

    /// <summary>True once nothing is pending.</summary>
    public bool Done => Pending == 0;
}
