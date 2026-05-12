using AwesomeAssertions;
using Humans.Application.Interfaces.Mailer;

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
}
