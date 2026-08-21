using System.Reflection;
using AwesomeAssertions;
using Humans.MailerLite.Services;

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
