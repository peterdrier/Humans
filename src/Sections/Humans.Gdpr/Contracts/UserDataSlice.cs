namespace Humans.Gdpr.Contracts;

/// <summary>One contributor's slice of the export.</summary>
/// <param name="SectionName">
/// Unique across all contributors (a duplicate throws) and a key of the contributor's
/// <see cref="IUserDataContributor.ErasureDeclaration"/>.
/// </param>
/// <param name="Data">
/// <c>null</c> only for a single-object section with no entity (the slice is dropped). A collection
/// section returns an empty list, which exports as <c>[]</c>.
/// </param>
public sealed record UserDataSlice(string SectionName, object? Data);
