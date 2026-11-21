using Humans.Base.Interfaces;

namespace Humans.Permissions.Contracts;

/// <summary>The permissions a section checks. Each section declares its own; a key declared twice fails the first check.</summary>
public interface ISectionPermissions : ISectionContribution
{
    IReadOnlyList<Permission> Permissions { get; }
}
