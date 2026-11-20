namespace Humans.Base.Attributes;

/// <summary>
/// Marks a controller that keeps answering, and keeps its links, while an admin has switched its section off for the
/// event (<c>ISectionSwitches</c>). For the rare page a member is owed whatever the organisation chooses to run:
/// their own personal-data export and deletion.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class StaysOnWhenSectionIsOffAttribute : Attribute;
