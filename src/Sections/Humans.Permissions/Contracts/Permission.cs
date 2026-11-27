namespace Humans.Permissions.Contracts;

/// <summary>Something a person may do on an owner, declared by the section that checks it.</summary>
/// <param name="Key">The stored key, <c>&lt;Section&gt;.&lt;Verb&gt;</c>, such as <c>Shifts.Manage</c>.</param>
/// <param name="Title">Resource key of the short label for the role form.</param>
/// <param name="Blurb">Resource key of what holding it lets a person do.</param>
/// <param name="OwnerKinds">The owner kinds it applies to, such as <c>Team</c>.</param>
/// <param name="TopLevelOnly">Only a holding on a top-level owner counts (it still reaches that owner's children).</param>
/// <param name="AdminRoles">System roles that hold it on every owner. Admin always does.</param>
/// <param name="Resource">The declaring section's resource marker, which holds <paramref name="Title"/> and
/// <paramref name="Blurb"/> in every culture. Whoever shows them localizes through it.</param>
public sealed record Permission(
    string Key,
    string Title,
    string Blurb,
    IReadOnlyList<string> OwnerKinds,
    bool TopLevelOnly,
    IReadOnlyList<string> AdminRoles,
    Type Resource);
