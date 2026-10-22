using System.ComponentModel.DataAnnotations;
using Humans.Finance.Contracts;
using Humans.Preapprovals.Contracts;
using Humans.Preapprovals.Domain;
using Humans.Preapprovals.Services;
using Humans.Users.Contracts;
using NodaTime;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Humans.Preapprovals.Models;

internal sealed class MemberIndexViewModel
{
    public required Guid CurrentUserId { get; init; }
    public required LocalDate Today { get; init; }
    public required IReadOnlyList<GrantInfo> Grants { get; init; }
    public required IReadOnlyList<ProgramInfo> OpenPrograms { get; init; }
    public required bool CanManage { get; init; }

    public IEnumerable<GrantInfo> Active => Grants.Where(g => !g.IsTerminal).OrderBy(g => g.ClaimBy);
    public IEnumerable<GrantInfo> Past => Grants.Where(g => g.IsTerminal).OrderByDescending(g => g.UpdatedAt);
}

internal sealed class MemberProgramViewModel
{
    public required ProgramInfo Program { get; init; }
    public required LocalDate Today { get; init; }
    public required IReadOnlyList<GrantInfo> MyGrants { get; init; }
    public required ApplyFormViewModel Form { get; init; }

    public GrantInfo? Pending => MyGrants.FirstOrDefault(g => g.Status == GrantStatus.Applied);
}

internal sealed class ApplyFormViewModel
{
    [StringLength(2000)]
    public string? Note { get; set; }

    [Range(0.01, 1_000_000)]
    public decimal? RequestedAmount { get; set; }
}

/// <summary>Every program, or only the ones the person manages.</summary>
internal sealed class AdminIndexViewModel
{
    public required LocalDate Today { get; init; }
    public required IReadOnlyList<ProgramInfo> Programs { get; init; }
    public required bool CanAdminister { get; init; }
}

internal sealed class AdminProgramViewModel
{
    public required ProgramInfo Program { get; init; }
    public required LocalDate Today { get; init; }
    public required bool CanAdminister { get; init; }
    public required IReadOnlyDictionary<Guid, UserInfo> People { get; init; }

    public string NameOf(Guid? userId) =>
        userId is { } id && People.TryGetValue(id, out var info) ? info.BurnerName : "—";

    public IEnumerable<GrantInfo> Applications => Program.Grants.Where(g => g.Status == GrantStatus.Applied);
    public IEnumerable<GrantInfo> Decided => Program.Grants.Where(g => g.Status != GrantStatus.Applied)
        .OrderBy(g => g.IsTerminal).ThenBy(g => g.CreatedAt);
}

/// <summary>Managers arrive as a list of ids from the people picker.</summary>
internal sealed class ProgramFormViewModel
{
    public Guid? Id { get; set; }

    [Required, StringLength(200)]
    public string Name { get; set; } = "";

    [StringLength(2000)]
    public string? Description { get; set; }

    [BindRequired]
    public ProgramKind Kind { get; set; } = ProgramKind.FixedPerPerson;

    [Range(0.01, 1_000_000)]
    public decimal? PerPersonAmount { get; set; }

    [Range(0.01, 100_000_000)]
    public decimal? TotalCeiling { get; set; }

    [Required]
    public int? HoldedAccountNumber { get; set; }

    [BindRequired]
    public bool AcceptsApplications { get; set; }

    [Required]
    public LocalDate? ClaimBy { get; set; }

    [BindRequired]
    public ProgramStatus Status { get; set; } = ProgramStatus.Open;

    public List<Guid> ManagerUserIds { get; set; } = [];

    /// <summary>Set by the controller for rendering; never posted.</summary>
    public IReadOnlyList<HoldedExpenseAccountOption> Accounts { get; set; } = [];
    public IReadOnlyDictionary<Guid, UserInfo> People { get; set; } = new Dictionary<Guid, UserInfo>();
    public bool KindLocked { get; set; }

    public ProgramSave ToSave() => new(
        Name, Description, Kind,
        Kind == ProgramKind.FixedPerPerson ? PerPersonAmount : null,
        TotalCeiling, HoldedAccountNumber!.Value, AcceptsApplications, ClaimBy!.Value, Status,
        ManagerUserIds.Distinct().ToList());

    public static ProgramFormViewModel From(ProgramInfo p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Description = p.Description,
        Kind = p.Kind,
        PerPersonAmount = p.PerPersonAmount,
        TotalCeiling = p.TotalCeiling,
        HoldedAccountNumber = p.HoldedAccountNumber,
        AcceptsApplications = p.AcceptsApplications,
        ClaimBy = p.ClaimBy,
        Status = p.Status,
        ManagerUserIds = p.ManagerUserIds.ToList(),
        KindLocked = p.KindLocked
    };
}
