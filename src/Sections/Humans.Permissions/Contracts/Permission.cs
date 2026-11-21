namespace Humans.Permissions.Contracts;

/// <summary>Something a person may do on an owner, declared by the section that checks it.</summary>
/// <param name="Key">The stored key, <c>&lt;Section&gt;.&lt;Verb&gt;</c>, such as <c>Shifts.Manage</c>.</param>
/// <param name="Title">Short label for the role form and the permissions page.</param>
/// <param name="Blurb">What holding it lets a person do.</param>
/// <param name="OwnerKinds">The owner kinds it applies to, such as <c>Team</c>.</param>
/// <param name="TopLevelOnly">Only a holding on a top-level owner counts (it still reaches that owner's children).</param>
/// <param name="AdminRoles">System roles that hold it on every owner. Admin always does.</param>
public sealed record Permission(
    string Key,
    string Title,
    string Blurb,
    IReadOnlyList<string> OwnerKinds,
    bool TopLevelOnly,
    IReadOnlyList<string> AdminRoles);
