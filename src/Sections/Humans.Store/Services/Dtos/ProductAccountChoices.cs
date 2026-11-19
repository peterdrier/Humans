namespace Humans.Store.Services.Dtos;

internal sealed record AccountChoice(int Number, string Name)
{
    public string Label => $"{Number} · {Name}";
}

/// <summary>The unarchived chart accounts each product account field may hold.</summary>
internal sealed record ProductAccountChoices(
    IReadOnlyList<AccountChoice> Income,
    IReadOnlyList<AccountChoice> Cost,
    IReadOnlyList<AccountChoice> TeamUsage)
{
    /// <summary>Why these accounts can't be saved, or null. An empty field is always allowed.</summary>
    public string? Refusal(int? income, int? cost, int? teamUsage) =>
        Missing("Income account", income, Income)
        ?? Missing("Cost account", cost, Cost)
        ?? Missing("Team usage account", teamUsage, TeamUsage);

    private static string? Missing(string label, int? account, IReadOnlyList<AccountChoice> choices) =>
        account is { } n && choices.All(c => c.Number != n)
            ? $"{label} {n} is no longer in the Holded chart (removed or archived). Pick another."
            : null;
}

internal sealed record ProductAccounts(Guid ProductId, int? Income, int? Cost, int? TeamUsage);
