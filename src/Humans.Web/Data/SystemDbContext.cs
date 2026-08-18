using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Humans.Web.Data;

/// <summary>
/// Platform database context (nobodies-collective/Humans#858): the home for
/// framework-owned tables that no section can plausibly own. Maps only
/// <c>DataProtectionKeys</c>, with its own <c>__EFMigrationsHistory_System</c>
/// table and migrations under <c>Migrations/System/</c>. Same database, same
/// connection — the split is a code-side partition of the EF model.
/// </summary>
/// <remarks>
/// <para>Internal-sealed like every section context (issue #750).</para>
/// <para>
/// Kept under glass (Peter, 2026-08-10 on #858): framework-owned tables only,
/// nothing a section could plausibly own, and adding a table here is Peter's
/// call — "it didn't fit anywhere else" is the argument that built the 644k-line
/// <c>HumansDbContext</c> migration history the first time (deleted at #858 peel 15).
/// </para>
/// </remarks>
internal sealed class SystemDbContext(DbContextOptions<SystemDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
}
