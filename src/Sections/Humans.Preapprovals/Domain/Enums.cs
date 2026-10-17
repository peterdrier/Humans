namespace Humans.Preapprovals.Domain;

/// <summary>How a program sizes its grants.</summary>
internal enum ProgramKind
{
    /// <summary>Every grant is the program's <c>PerPersonAmount</c> (travel: 250 € each).</summary>
    FixedPerPerson = 0,

    /// <summary>The amount is set on each grant (creativity: one figure per project).</summary>
    PerGrantAmount = 1,
}

/// <summary>
/// Open takes grants and applications; Closed takes neither. Closing never touches an issued
/// grant, which stays claimable until the program's claim deadline.
/// </summary>
internal enum ProgramStatus
{
    Open = 0,
    Closed = 1,
}

/// <summary>Which deadline reminder a <see cref="ReminderSent"/> row records.</summary>
internal enum ReminderKind
{
    D14 = 0,
    D3 = 1,
}
