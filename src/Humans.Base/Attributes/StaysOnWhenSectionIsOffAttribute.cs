namespace Humans.Base.Attributes;

/// <summary>
/// Marks a controller that keeps answering, and keeps its links, while an admin has switched its section off for the
/// event (<c>ISectionSwitches</c>). For the rare page a member is owed whatever the organisation chooses to run:
/// their own personal-data export and deletion.
/// </summary>
/// <remarks>
/// On a recurring job, it keeps the job running while its section is off: data retention, and work other sections
/// lean on (the email outbox, the legal documents admission needs).
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class StaysOnWhenSectionIsOffAttribute : Attribute;
