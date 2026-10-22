namespace Humans.Preapprovals.Domain;

internal enum ProgramKind
{
    /// <summary>Every grant is the program's <c>PerPersonAmount</c> (travel: 250 € each).</summary>
    FixedPerPerson = 0,

    /// <summary>The amount is set on each grant (creativity: one figure per project).</summary>
    PerGrantAmount = 1,
}

/// <summary>Closing never touches an issued grant, which stays claimable until the claim deadline.</summary>
internal enum ProgramStatus
{
    Open = 0,
    Closed = 1,
}

internal enum ReminderKind
{
    D14 = 0,
    D3 = 1,
}
