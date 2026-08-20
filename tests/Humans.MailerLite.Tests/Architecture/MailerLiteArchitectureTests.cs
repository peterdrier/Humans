using System.Reflection;
using AwesomeAssertions;
using Humans.MailerLite.Domain;
using Humans.MailerLite.Services;
using Microsoft.EntityFrameworkCore;

namespace Humans.MailerLite.Tests.Architecture;

public class MailerLiteArchitectureTests
{
    private static Assembly SectionAssembly => typeof(MailerLiteImportService).Assembly;

    [HumansFact]
    public void IMailerLiteService_OnlyAllowsAudienceWrites()
    {
        var allowedWrites = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(IMailerLiteService.CreateGroupAsync),
            nameof(IMailerLiteService.AssignSubscriberToGroupAsync),
            nameof(IMailerLiteService.UnassignSubscriberFromGroupAsync),
            nameof(IMailerLiteService.BulkImportSubscribersToGroupAsync),
        };

        var writePrefixes = new[]
        {
            "Create", "Update", "Delete", "Upsert", "Add", "Remove",
            "Set", "Post", "Put", "Patch", "Assign", "Unassign", "Bulk",
        };

        var unexpectedWrites = typeof(IMailerLiteService).GetMethods()
            .Where(m => writePrefixes.Any(p => m.Name.StartsWith(p, StringComparison.Ordinal)))
            .Where(m => !allowedWrites.Contains(m.Name))
            .Select(m => m.Name)
            .ToList();

        unexpectedWrites.Should().BeEmpty(
            "IMailerLiteService writes are restricted to the four audience-management methods. " +
            "New writes need their own architecture review.");
    }

    [HumansFact]
    public void IMailerLiteService_LivesInMailerLiteNamespace()
    {
        typeof(IMailerLiteService).Namespace
            .Should().Be("Humans.MailerLite.Services");
    }

    /// <summary>
    /// Only <c>Data/</c> may touch EF. The section gained a DbContext in
    /// nobodies-collective/Humans#1082, which retired the assembly-wide EF-free rule this
    /// replaces — but the services must still reach the table through the repository.
    /// </summary>
    [HumansFact]
    public void OnlyTheDataFolder_TouchesEFCore()
    {
        var offenders = SectionAssembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("Humans.MailerLite.Data", StringComparison.Ordinal) != true)
            .Where(t => t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Any(f => typeof(DbContext).IsAssignableFrom(f.FieldType)
                          || (f.FieldType.IsGenericType
                              && f.FieldType.GetGenericTypeDefinition() == typeof(IDbContextFactory<>))))
            .Select(t => t.FullName)
            .ToList();

        offenders.Should().BeEmpty(
            "only the repository under Data/ may hold a MailerLiteDbContext");
    }

    /// <summary>
    /// <c>mailerlite_sync_states</c> is one row per key, and the reconciliation run holds a
    /// reserved one — an audience claiming it would overwrite that row.
    /// </summary>
    [HumansFact]
    public void NoAudience_ClaimsTheReservedReconciliationKey()
    {
        AudienceInstances().Should().NotContain(
            a => string.Equals(a.Key, MailerLiteSyncKeys.Reconciliation, StringComparison.Ordinal));
    }

    [HumansFact]
    public void AllAudiences_UseHumansPrefix()
    {
        var impls = AudienceInstances();

        impls.Should().NotBeEmpty("at least one IMailerLiteAudience implementation is expected.");

        foreach (var instance in impls)
        {
            instance.MailerLiteGroupName.Should().StartWith("Humans - ",
                $"every IMailerLiteAudience must target a Humans-prefixed group; {instance.GetType().Name} does not.");
        }
    }

    [HumansFact]
    public void AllAudiences_HaveUniqueGroupNamesAndKeys()
    {
        var impls = AudienceInstances();

        impls.Select(a => a.Key).Distinct(StringComparer.Ordinal).Count().Should().Be(impls.Count,
            "audience keys collide");
        impls.Select(a => a.MailerLiteGroupName).Distinct(StringComparer.Ordinal).Count().Should().Be(impls.Count,
            "audience group names collide");
    }

    [HumansFact]
    public void MailerLiteAudienceSyncService_LivesInSectionServicesNamespace()
    {
        typeof(MailerLiteAudienceSyncService).Namespace.Should().Be("Humans.MailerLite.Services");
    }

    private static IReadOnlyList<IMailerLiteAudience> AudienceInstances()
    {
        var audienceType = typeof(IMailerLiteAudience);
        return SectionAssembly
            .GetTypes()
            .Where(t => audienceType.IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false })
            .Select(t => (IMailerLiteAudience)Activator.CreateInstance(t, NonPublicConstructorBypass(t))!)
            .ToList();
    }

    // Reflection helper — passes null/default args to allow constructing audiences
    // that take service dependencies. The arch test only inspects metadata properties.
    private static object?[] NonPublicConstructorBypass(Type t)
    {
        var ctor = t.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        return ctor.GetParameters().Select(p =>
            p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
    }
}
