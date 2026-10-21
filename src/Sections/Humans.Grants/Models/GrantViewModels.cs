using System.ComponentModel.DataAnnotations;
using Humans.Grants.Domain;
using Humans.Grants.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using NodaTime;

namespace Humans.Grants.Models;

/// <summary>The admin index: every program with its counts, or only the ones the person manages.</summary>
internal sealed class AdminIndexViewModel
{
    public required LocalDate Today { get; init; }
    public required IReadOnlyList<ProgramInfo> Programs { get; init; }
    public required bool CanAdminister { get; init; }
}

/// <summary>One program's manage page: its grants and the issue form.</summary>
internal sealed class AdminProgramViewModel
{
    public required ProgramInfo Program { get; init; }
    public required LocalDate Today { get; init; }
    public required bool CanAdminister { get; init; }
    public required IReadOnlyDictionary<Guid, UserInfo> People { get; init; }

    public string NameOf(Guid? userId) =>
        userId is { } id && People.TryGetValue(id, out var info) ? info.BurnerName : "—";

    public IEnumerable<GrantInfo> Grants => Program.Grants
        .OrderBy(g => g.State(Today)).ThenByDescending(g => g.IssuedAt);
}

/// <summary>The create/edit form. Managers arrive as a list of ids from the people picker.</summary>
internal sealed class ProgramFormViewModel
{
    public Guid? Id { get; set; }

    [Required, StringLength(200)]
    public string Name { get; set; } = "";

    [Required, StringLength(500)]
    public string Entitles { get; set; } = "";

    [StringLength(2000)]
    public string? Description { get; set; }

    [Required, Range(2000, 2100)]
    public int? Year { get; set; }

    [BindRequired]
    public GrantIssuer IssuedBy { get; set; } = GrantIssuer.Managers;

    [StringLength(32)]
    public string? SourceKey { get; set; }

    public LocalDate? GrantsExpireOn { get; set; }

    [BindRequired]
    public GrantProgramStatus Status { get; set; } = GrantProgramStatus.Open;

    public List<Guid> ManagerUserIds { get; set; } = [];

    /// <summary>Set by the controller for rendering; never posted.</summary>
    public IReadOnlyDictionary<Guid, UserInfo> People { get; set; } = new Dictionary<Guid, UserInfo>();

    public ProgramSave ToSave() => new(
        Name, Entitles, Description, Year!.Value, IssuedBy, SourceKey, GrantsExpireOn, Status,
        ManagerUserIds.Distinct().ToList());

    public static ProgramFormViewModel From(ProgramInfo p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Entitles = p.Entitles,
        Description = p.Description,
        Year = p.Year,
        IssuedBy = p.IssuedBy,
        SourceKey = p.SourceKey,
        GrantsExpireOn = p.GrantsExpireOn,
        Status = p.Status,
        ManagerUserIds = p.ManagerUserIds.ToList()
    };
}
