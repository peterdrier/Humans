namespace Humans.Domain.Attributes;

/// <summary>
/// Declares the section a type belongs to when the section is not derivable
/// from the type's namespace. Used by HUM0017 to detect cross-section
/// repository injection: a service in <c>Humans.Application.Services.{X}</c>
/// must not depend on a repository interface marked <c>[Section("Y")]</c>
/// when X != Y.
/// </summary>
/// <remarks>
/// The remaining <c>Humans.Application</c> repository interfaces live flat under
/// <c>Humans.Application.Interfaces.Repositories</c>, and a section's own live in
/// its <c>Data/</c> namespace, so neither interface namespace carries section
/// information. The
/// implementation's namespace is the source of truth; this attribute mirrors
/// that section onto the interface so the Application-layer analyzer can read
/// it without referencing Humans.Infrastructure.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Interface | AttributeTargets.Class)]
public sealed class SectionAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
