using AwesomeAssertions;
using Humans.Application.Interfaces.Admin;
using Humans.Web.Services;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace Humans.Application.Tests.Services;

public class AdminDashboardServiceTests
{
    // Helper: create a LogEvent at a given level and timestamp
    private static LogEvent MakeEvent(LogEventLevel level, DateTimeOffset timestamp) =>
        new(timestamp, level, null, new MessageTemplateParser().Parse("test"), []);

    [HumansFact]
    public void CountErrorsSince_returns_count_of_Error_and_Fatal_events_since_threshold()
    {
        var threshold = DateTimeOffset.UtcNow.AddHours(-24);
        var now = DateTimeOffset.UtcNow;
        var old = now.AddHours(-25);

        var events = new List<LogEvent>
        {
            MakeEvent(LogEventLevel.Error,   now),          // within window
            MakeEvent(LogEventLevel.Fatal,   now),          // within window
            MakeEvent(LogEventLevel.Error,   threshold),    // exactly at boundary — included
            MakeEvent(LogEventLevel.Warning, now),          // wrong level — excluded
            MakeEvent(LogEventLevel.Error,   old),          // too old — excluded
        };

        var count = AdminDashboardService.CountErrorsSince(events, threshold);

        count.Should().Be(3);
    }

    [HumansFact]
    public void CountErrorsSince_returns_zero_for_empty_list()
    {
        var count = AdminDashboardService.CountErrorsSince([], DateTimeOffset.UtcNow.AddHours(-24));

        count.Should().Be(0);
    }

    [HumansFact]
    public void AdminSystemHealth_AllNormal_is_true_when_zero_errors_and_zero_jobs()
    {
        var health = new AdminSystemHealth(ErrorsLast24h: 0, FailedJobs: 0);

        health.AllNormal.Should().BeTrue();
    }

    [HumansFact]
    public void AdminSystemHealth_AllNormal_is_false_when_any_errors()
    {
        var health = new AdminSystemHealth(ErrorsLast24h: 1, FailedJobs: 0);

        health.AllNormal.Should().BeFalse();
    }

    [HumansFact]
    public void AdminSystemHealth_AllNormal_is_false_when_any_failed_jobs()
    {
        var health = new AdminSystemHealth(ErrorsLast24h: 0, FailedJobs: 3);

        health.AllNormal.Should().BeFalse();
    }
}
