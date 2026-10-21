using System.Text.Json;
using AwesomeAssertions;
using Humans.Grants.Domain;
using Humans.Grants.Services;
using Humans.Grants.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Humans.Grants.Tests.Services;

/// <summary>
/// Every column of every Grants table is classified here: exported under a named export key,
/// a person id kept on erasure (named in the retention text), or explicitly not personal. A new
/// column fails <see cref="EveryColumn_IsClassified"/> until someone decides which it is.
/// </summary>
public sealed class GdprCoverageTests : GrantsTestHarness
{
    /// <summary>Exported columns: the export key that must carry a value for a fully seeded person.</summary>
    private static readonly Dictionary<string, string> Exported = new(StringComparer.Ordinal)
    {
        [$"{nameof(Grant)}.{nameof(Grant.UserId)}"] = "Program",
        [$"{nameof(Grant)}.{nameof(Grant.SourceRef)}"] = "SourceRef",
        [$"{nameof(Grant)}.{nameof(Grant.ExpiresOn)}"] = "ExpiresOn",
        [$"{nameof(Grant)}.{nameof(Grant.IssuedAt)}"] = "IssuedAt",
        [$"{nameof(Grant)}.{nameof(Grant.NotifiedAt)}"] = "NotifiedAt",
        [$"{nameof(Grant)}.{nameof(Grant.RevokedAt)}"] = "RevokedAt",
        [$"{nameof(Grant)}.{nameof(Grant.RevokeReason)}"] = "RevokeReason",
        [$"{nameof(GrantProgram)}.{nameof(GrantProgram.CreatedAt)}"] = "CreatedAt",
        [$"{nameof(GrantProgramManager)}.{nameof(GrantProgramManager.UserId)}"] = "ManagerOf",
        [$"{nameof(GrantProgramManager)}.{nameof(GrantProgramManager.AddedAt)}"] = "AddedAt",
    };

    /// <summary>Actor ids: exported under a decisions key, kept on erasure, named in the retention text.</summary>
    private static readonly Dictionary<string, (string Key, string Retention)> Actors = new(StringComparer.Ordinal)
    {
        [$"{nameof(Grant)}.{nameof(Grant.IssuedByUserId)}"] = ("GrantsIssued", "grants they issued"),
        [$"{nameof(Grant)}.{nameof(Grant.RevokedByUserId)}"] = ("GrantsRevoked", "or revoked"),
        [$"{nameof(GrantProgram)}.{nameof(GrantProgram.CreatedByUserId)}"] = ("ProgramsCreated", "programs they created"),
        [$"{nameof(GrantProgramManager)}.{nameof(GrantProgramManager.AddedByUserId)}"] = ("ManagersNamed", "managers they named"),
    };

    /// <summary>Keys and program facts: nothing about a person.</summary>
    private static readonly HashSet<string> NotPersonal = new(StringComparer.Ordinal)
    {
        $"{nameof(Grant)}.{nameof(Grant.Id)}",
        $"{nameof(Grant)}.{nameof(Grant.ProgramId)}",
        $"{nameof(GrantProgram)}.{nameof(GrantProgram.Id)}",
        $"{nameof(GrantProgram)}.{nameof(GrantProgram.Name)}",
        $"{nameof(GrantProgram)}.{nameof(GrantProgram.Entitles)}",
        $"{nameof(GrantProgram)}.{nameof(GrantProgram.Description)}",
        $"{nameof(GrantProgram)}.{nameof(GrantProgram.Year)}",
        $"{nameof(GrantProgram)}.{nameof(GrantProgram.IssuedBy)}",
        $"{nameof(GrantProgram)}.{nameof(GrantProgram.SourceKey)}",
        $"{nameof(GrantProgram)}.{nameof(GrantProgram.GrantsExpireOn)}",
        $"{nameof(GrantProgram)}.{nameof(GrantProgram.Status)}",
        $"{nameof(GrantProgram)}.{nameof(GrantProgram.UpdatedAt)}",
        $"{nameof(GrantProgramManager)}.{nameof(GrantProgramManager.ProgramId)}",
    };

    [HumansFact]
    public void EveryColumn_IsClassified()
    {
        var columns = Db.Model.GetEntityTypes()
            .SelectMany(t => t.GetProperties().Select(p => $"{t.ClrType.Name}.{p.Name}"))
            .ToList();

        columns.Should().BeEquivalentTo(Exported.Keys.Concat(Actors.Keys).Concat(NotPersonal));
    }

    [HumansFact]
    public async Task EveryPersonalColumn_ReachesTheExport_AndActorsTheRetentionText()
    {
        var person = SeedUser("Person");
        var program = await SeedProgramAsync(expiresOn: Today.PlusDays(30), managers: person);
        await using (var db = OpenContext())
        {
            (await db.Programs.SingleAsync(Ct)).CreatedByUserId = person;
            (await db.Managers.SingleAsync(Ct)).AddedByUserId = person;
            await db.SaveChangesAsync(Ct);
        }
        var grant = await SeedGrantAsync(program, person, expiresOn: Today.PlusDays(10), revoked: true);
        await using (var db = OpenContext())
        {
            var row = await db.Grants.SingleAsync(g => g.Id == grant.Id, Ct);
            row.SourceRef = "ref-1";
            row.RevokeReason = "reason";
            row.IssuedByUserId = person;
            row.RevokedByUserId = person;
            await db.SaveChangesAsync(Ct);
        }

        var service = NewService();
        var slices = await service.ContributeForUserAsync(person, Ct);
        var filled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slice in slices)
            Collect(JsonSerializer.SerializeToElement(slice.Data), filled);

        filled.Should().Contain(Exported.Values.Concat(Actors.Values.Select(a => a.Key)));
        foreach (var (_, retention) in Actors.Values)
            service.ErasureDeclaration[GrantService.GrantDecisions].Should().Contain(retention);
    }

    /// <summary>Every property name in the export whose value is neither null nor an empty list.</summary>
    private static void Collect(JsonElement element, HashSet<string> filled)
    {
        if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) Collect(item, filled);
        if (element.ValueKind != JsonValueKind.Object) return;
        foreach (var property in element.EnumerateObject())
        {
            var value = property.Value;
            if (value.ValueKind is JsonValueKind.Null || (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0))
                continue;
            filled.Add(property.Name);
            Collect(value, filled);
        }
    }
}
