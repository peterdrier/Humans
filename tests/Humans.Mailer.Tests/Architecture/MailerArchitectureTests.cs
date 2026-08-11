using System.Reflection;
using AwesomeAssertions;
using Humans.Mailer.Services;
using Microsoft.Extensions.Localization;

namespace Humans.Mailer.Tests.Architecture;

public class MailerArchitectureTests
{
    private static Assembly SectionAssembly => typeof(MailerImportService).Assembly;

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
    public void IMailerLiteService_LivesInMailerNamespace()
    {
        typeof(IMailerLiteService).Namespace
            .Should().Be("Humans.Mailer.Services");
    }

    /// <summary>
    /// Mailer owns no tables, so the whole section assembly is EF-free — not just the two
    /// orchestrators the pre-G5 version of this test named while they sat in
    /// <c>Humans.Application</c>. Restated on the section assembly rather than deleted:
    /// Calendar's rule is to keep the invariant and re-aim it, and here the wider form is
    /// the honest one because there is no repository to legitimise a reference.
    /// </summary>
    [HumansFact]
    public void SectionAssembly_DoesNotReferenceEFCore()
    {
        SectionAssembly.GetReferencedAssemblies()
            .Should().NotContain(a => string.Equals(a.Name, "Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
    }

    /// <summary>
    /// Mailer ships no resource set at all — its two admin pages are English operator copy
    /// with not one <c>Localizer[…]</c> call — so the guard is Gate's stricter form: no type
    /// in the section may take <see cref="IStringLocalizer{T}"/> for <em>any</em>
    /// <c>T</c>. The day someone adds localized copy, the build says "carve a resource set
    /// first" instead of silently resolving against Shell's ambient
    /// <c>SharedResource</c>, which a section RCL cannot see anyway.
    /// </summary>
    [HumansFact]
    public void SectionTypesTakeNoStringLocalizer()
    {
        var offenders = SectionAssembly.GetTypes()
            .SelectMany(t => t.GetConstructors().Select(c => (Type: t, Ctor: c)))
            .SelectMany(x => x.Ctor.GetParameters().Select(p => (x.Type, p.ParameterType)))
            .Where(x => x.ParameterType.IsGenericType
                        && x.ParameterType.GetGenericTypeDefinition() == typeof(IStringLocalizer<>))
            .Select(x => x.Type.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            "Mailer has no Resources/ folder and no MailerResource marker. A type binding a " +
            "localizer here is either reaching for a set the RCL cannot see, or a sign the " +
            "section now needs its own.");
    }

    [HumansFact]
    public void AllAudiences_UseHumansPrefix()
    {
        var impls = AudienceInstances();

        impls.Should().NotBeEmpty("at least one IMailerAudience implementation is expected.");

        foreach (var instance in impls)
        {
            instance.MailerLiteGroupName.Should().StartWith("Humans - ",
                $"every IMailerAudience must target a Humans-prefixed group; {instance.GetType().Name} does not.");
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
    public void MailerAudienceSyncService_LivesInSectionServicesNamespace()
    {
        typeof(MailerAudienceSyncService).Namespace.Should().Be("Humans.Mailer.Services");
    }

    private static IReadOnlyList<IMailerAudience> AudienceInstances()
    {
        var audienceType = typeof(IMailerAudience);
        return SectionAssembly
            .GetTypes()
            .Where(t => audienceType.IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false })
            .Select(t => (IMailerAudience)Activator.CreateInstance(t, NonPublicConstructorBypass(t))!)
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
