using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Configuration;
using Humans.Email.Contracts;
using Humans.Email.Controllers;
using Humans.Email.Models;
using Humans.Email.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.Extensions;

namespace Humans.Email.Tests.Controllers;

/// <summary>
/// The gallery renders registered <see cref="IEmailPreviewContributor"/>s alongside the
/// legacy static table while the templates move to their sending sections
/// (peterdrier/Humans#1651).
/// </summary>
public sealed class EmailPreviewGalleryTests
{
    private const string LegacyId = "application-approved";

    private sealed class StubContributor(params string[] ids) : IEmailPreviewContributor
    {
        public List<EmailPreviewPersona> Asked { get; } = [];

        public IReadOnlyList<EmailPreviewSample> Samples(EmailPreviewPersona persona)
        {
            Asked.Add(persona);
            return [.. ids.Select(id => new EmailPreviewSample(id, $"Sample {id}",
                new EmailMessage(persona.Email, persona.Name, $"Subject {id}", "<p>Contributed</p>", id)))];
        }
    }

    private static EmailPreviewViewModel Render(params IEmailPreviewContributor[] contributors)
    {
        var renderer = Substitute.For<IEmailRenderer>();
        renderer.ReturnsForAll(new EmailContent("Legacy subject", "<p>Legacy</p>"));

        var composer = Substitute.For<IEmailBodyComposer>();
        composer.Compose(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(call => ($"[wrapped]{call.ArgAt<string>(0)}", "plain"));

        var controller = new EmailController(
            Substitute.For<IUserServiceRead>(),
            Substitute.For<IEmailOutboxService>(),
            Substitute.For<IAuditLogService>(),
            NullLogger<EmailController>.Instance);

        var result = controller.EmailPreview(
            renderer, composer, Options.Create(new EmailSettings()), contributors);

        return (EmailPreviewViewModel)((ViewResult)result).Model!;
    }

    [HumansFact]
    public void ContributedSamples_RenderInEveryCulture_AskedWithThatCulturesPersona()
    {
        var contributor = new StubContributor("assembly-vote-opened");

        var model = Render(contributor);

        contributor.Asked.Select(p => p.Culture).Should().BeEquivalentTo(model.Previews.Keys);
        contributor.Asked.Should().AllSatisfy(p => p.Email.Should().NotBeNullOrWhiteSpace());
        foreach (var (culture, items) in model.Previews)
        {
            var sample = items.Should().ContainSingle(i => i.Id == "assembly-vote-opened").Subject;
            sample.Name.Should().Be("Sample assembly-vote-opened");
            sample.Subject.Should().Be("Subject assembly-vote-opened");
            sample.Body.Should().Be("[wrapped]<p>Contributed</p>", $"the gallery wraps contributed bodies too ({culture})");
        }
    }

    [HumansFact]
    public void LegacyTableStillRenders_AndAContributedSampleSupersedesItsLegacyRow()
    {
        var withoutContributor = Render();
        withoutContributor.Previews["en"].Should().Contain(i => i.Id == LegacyId);

        var model = Render(new StubContributor(LegacyId));

        var items = model.Previews["en"];
        items.Should().ContainSingle(i => i.Id == LegacyId)
            .Subject.Subject.Should().Be($"Subject {LegacyId}");
        items.Count.Should().Be(withoutContributor.Previews["en"].Count);
    }
}
