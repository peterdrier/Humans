using AwesomeAssertions;
using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using Humans.Web.Extensions;

namespace Humans.Web.Tests.Infrastructure;

public sealed class SwitchedOffSectionJobsTests
{
    // A type in this test assembly belongs to the section "Web.Tests".
    private static readonly string ThisSection = SectionSwitchboard.SectionNameOf(typeof(SectionJob));

    private static readonly RecurringJobExtensions.ScheduledJob[] Jobs =
    [
        RecurringJobExtensions.ToScheduledJob(new RecurringJobDescriptor("section-job", typeof(SectionJob), "0 5 * * *")),
        RecurringJobExtensions.ToScheduledJob(new RecurringJobDescriptor("retention-job", typeof(RetentionJob), "0 5 * * *")),
        RecurringJobExtensions.ToScheduledJob(new RecurringJobDescriptor("configured-off", typeof(SectionJob), "")),
    ];

    [HumansFact]
    public void Changed_PausesASwitchedOffSectionsJobsAndResumesThemWhenItIsBack()
    {
        var none = Set();
        var off = Set(ThisSection);

        SwitchedOffSectionJobs.Changed(Jobs, none, off).Select(c => (c.Job.Id, c.SwitchedOff))
            .Should().Equal(("section-job", true));
        SwitchedOffSectionJobs.Changed(Jobs, off, none).Select(c => (c.Job.Id, c.SwitchedOff))
            .Should().Equal(("section-job", false));
        SwitchedOffSectionJobs.Changed(Jobs, off, Set(ThisSection, "Surveys")).Should().BeEmpty();
    }

    private static HashSet<string> Set(params string[] names) => names.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public sealed class SectionJob
    {
        public Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [StaysOnWhenSectionIsOff]
    public sealed class RetentionJob
    {
        public Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
