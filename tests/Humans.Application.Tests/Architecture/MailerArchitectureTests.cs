using AwesomeAssertions;
using Humans.Application.Interfaces.Mailer;
using Humans.Application.Services.Mailer;

namespace Humans.Application.Tests.Architecture;

public class MailerArchitectureTests
{
    [HumansFact]
    public void IMailerLiteService_HasNoWriteMethods()
    {
        string[] forbidden = ["Create", "Update", "Delete", "Upsert", "Add", "Remove", "Set", "Post", "Put", "Patch"];
        var methods = typeof(IMailerLiteService).GetMethods();
        methods.Should().NotBeEmpty();
        foreach (var m in methods)
            foreach (var prefix in forbidden)
                m.Name.Should().NotStartWith(prefix,
                    $"IMailerLiteService is read-only by design; '{m.Name}' looks like a write method.");
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
