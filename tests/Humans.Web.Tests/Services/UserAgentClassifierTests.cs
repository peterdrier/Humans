using AwesomeAssertions;
using Humans.Infrastructure.Services;
using Xunit;

namespace Humans.Web.Tests.Services;

public class UserAgentClassifierTests
{
    private const string WinChrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private const string MacSafari = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.1 Safari/605.1.15";
    private const string IPhoneSafari = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.1 Mobile/15E148 Safari/604.1";
    private const string AndroidChrome = "Mozilla/5.0 (Linux; Android 13; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36";
    private const string LinuxFirefox = "Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:121.0) Gecko/20100101 Firefox/121.0";
    private const string WinEdge = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0";

    [HumansTheory]
    [InlineData(WinChrome, "Windows", "Desktop")]
    [InlineData(MacSafari, "macOS", "Desktop")]
    [InlineData(IPhoneSafari, "iOS", "Mobile")]
    [InlineData(AndroidChrome, "Android", "Mobile")]
    [InlineData(LinuxFirefox, "Linux", "Desktop")]
    [InlineData(WinEdge, "Windows", "Desktop")]
    public void Classify_MapsOsAndDevice(string ua, string expectedOs, string expectedDevice)
    {
        var result = UserAgentClassifier.Classify(ua);
        result.Os.Should().Be(expectedOs);
        result.Device.Should().Be(expectedDevice);
    }

    [HumansFact]
    public void Classify_DetectsCommonBrowserFamilies()
    {
        UserAgentClassifier.Classify(WinChrome).Browser.Should().Contain("Chrome");
        UserAgentClassifier.Classify(LinuxFirefox).Browser.Should().Contain("Firefox");
    }

    [HumansFact]
    public void Classify_Googlebot_CollapsesToBot()
    {
        const string ua = "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)";

        var result = UserAgentClassifier.Classify(ua);

        result.Os.Should().Be("Bot");
        result.Browser.Should().Be("Bot");
        result.Device.Should().Be("Bot");
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_BlankUserAgent_IsUnknown(string? ua)
    {
        var result = UserAgentClassifier.Classify(ua);

        result.Os.Should().Be("Unknown");
        result.Browser.Should().Be("Unknown");
        result.Device.Should().Be("Unknown");
    }
}
