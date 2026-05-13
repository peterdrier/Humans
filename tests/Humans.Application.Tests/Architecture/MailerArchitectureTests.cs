using AwesomeAssertions;
using Humans.Application.Interfaces.Mailer;
using Humans.Application.Services.Mailer;

namespace Humans.Application.Tests.Architecture;

public class MailerArchitectureTests
{
    [HumansFact]
    public void IMailerLiteService_OnlyAllowsAudienceWrites()
    {
        var allowedWrites = new HashSet<string>
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
            .Should().Be("Humans.Application.Interfaces.Mailer");
    }

    [HumansFact]
    public void MailerImportService_DoesNotReferenceEFCore()
    {
        var asm = typeof(MailerImportService).Assembly;
        asm.GetReferencedAssemblies()
            .Should().NotContain(a => string.Equals(a.Name, "Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
    }

    [HumansFact]
    public void MailerImportService_Constructor_HasNoCrossSectionRepositories()
    {
        var ctor = typeof(MailerImportService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();
        var forbiddenRepos = paramTypes
            .Where(t => t.Name.EndsWith("Repository", StringComparison.Ordinal))
            .ToList();
        forbiddenRepos.Should().BeEmpty(
            "MailerImportService is the orchestrator — it talks to other sections through service interfaces, not their repositories.");
    }

    [HumansFact]
    public void IMailerLiteService_LivesInApplication_Interfaces()
    {
        typeof(IMailerLiteService).Assembly.GetName().Name
            .Should().Be("Humans.Application");
    }
}
