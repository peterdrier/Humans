using System.Reflection;
using Humans.Base.Interfaces;

namespace Humans.Notifications.Contracts;

/// <summary>
/// A section's notification sources. Notifications collects every registered contributor at startup into one
/// registry, which rejects duplicate or over-long names and drives the inbox filters and the retired-source purge.
/// </summary>
public interface INotificationSourceContributor : IFanout
{
    IReadOnlyList<NotificationSourceDefinition> Sources { get; }
}

/// <summary>
/// Derive from this, declare each source as a <c>public static readonly</c> field, and register the class as an
/// <see cref="INotificationSourceContributor"/> singleton. The fields are the contributed list, so nothing is
/// declared twice.
/// </summary>
public abstract class NotificationSourceContributor : INotificationSourceContributor
{
    public IReadOnlyList<NotificationSourceDefinition> Sources { get; }

    protected NotificationSourceContributor()
    {
        Sources = GetType()
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => f.GetValue(null))
            .OfType<NotificationSourceDefinition>()
            .ToList();
    }
}
