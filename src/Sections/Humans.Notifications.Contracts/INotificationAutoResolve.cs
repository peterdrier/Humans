namespace Humans.Notifications.Contracts;

public interface INotificationAutoResolve
{
    /// <summary>With a <paramref name="sourceKey"/>, only that entity's notifications resolve.</summary>
    Task ResolveBySourceAsync(
        Guid userId, NotificationSourceDefinition source,
        CancellationToken ct = default, string? sourceKey = null);

    /// <summary>Across every recipient. <paramref name="resolvedByUserId"/> is recorded as the resolver.</summary>
    Task ResolveBySourceKeyAsync(
        NotificationSourceDefinition source, string sourceKey, Guid? resolvedByUserId,
        CancellationToken ct = default);
}
