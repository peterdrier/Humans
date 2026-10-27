using Humans.Base.Constants;
using Humans.Base.Interfaces;

namespace Humans.Auth;

/// <summary>The two system-wide roles. Their names live in Base's <see cref="RoleNames"/>; Auth declares them.</summary>
internal sealed class SectionRoles : ISectionRoles
{
    public IReadOnlyList<RoleDefinition> Roles { get; } =
    [
        new(RoleNames.Board, "Board",
            "Elected stewards of the collective. They guide strategy, approve tier applications, and make the big decisions so the rest of us can focus on making things happen.",
            "fa-solid fa-crown"),
        new(RoleNames.Admin, "Admin",
            "The ones who keep the lights on. Full system access, all the keys, all the responsibility. When something breaks at 3am, these are the humans who fix it.",
            "fa-solid fa-gear"),
    ];
}
