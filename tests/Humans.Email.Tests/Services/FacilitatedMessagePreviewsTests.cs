using System.Reflection;
using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Email.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Email.Tests.Services;

/// <summary>
/// Gallery-coverage gate for Email's own contribution: every template
/// <see cref="EmailMessageFactory"/> can build has a sample in
/// <see cref="FacilitatedMessagePreviews"/>, so a new template cannot ship invisible
/// (peterdrier/Humans#1651).
/// </summary>
public sealed class FacilitatedMessagePreviewsTests
{
    [HumansFact]
    public void EveryTemplateHasAPreviewSample()
    {
        var factory = CreateFactory();
        var templates = TemplateNames(factory);
        templates.Should().NotBeEmpty();

        var sampled = new FacilitatedMessagePreviews(factory)
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"))
            .Select(s => s.Message.TemplateName);

        // The samples contain the same template twice (the two arms), so compare
        // distinct sets rather than exact multisets.
        sampled.Distinct(StringComparer.Ordinal).Should().BeEquivalentTo(templates.Distinct(StringComparer.Ordinal),
            "every Email template needs a row in /Email/EmailPreview");
    }

    [HumansFact]
    public void PreviewSampleIdsAreUnique()
    {
        var samples = new FacilitatedMessagePreviews(CreateFactory())
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"));

        samples.Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Every template the factory can produce, found by reflection rather than listed, so
    /// a template added without a gallery sample fails <see cref="EveryTemplateHasAPreviewSample"/>.
    /// </summary>
    private static IReadOnlyList<string> TemplateNames(EmailMessageFactory factory) =>
    [
        .. typeof(EmailMessageFactory)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.ReturnType == typeof(EmailMessage))
            .Select(m => ((EmailMessage)m.Invoke(factory, [.. m.GetParameters().Select(SampleArgument)])!).TemplateName)
    ];

    private static object SampleArgument(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (type == typeof(string)) return "x";
        if (type == typeof(bool)) return true;
        throw new NotSupportedException(
            $"EmailMessageFactory.{parameter.Member.Name} takes a {type.Name}; teach this test how to sample one.");
    }

    private static EmailMessageFactory CreateFactory()
    {
        var localizer = Substitute.For<IStringLocalizer<EmailResource>>();
        localizer[Arg.Any<string>()].Returns(call =>
        {
            var key = call.Arg<string>();
            return new LocalizedString(key, key);
        });

        return new EmailMessageFactory(localizer, NullLogger<EmailMessageFactory>.Instance);
    }
}
