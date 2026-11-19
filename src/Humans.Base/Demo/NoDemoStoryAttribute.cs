namespace Humans.Base.Demo;

/// <summary>
/// A section that owns data but contributes no <see cref="IDemoStory"/>, and why. Every section with a DbContext
/// carries either a story or this.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class NoDemoStoryAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}
