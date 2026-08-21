using System.Runtime.CompilerServices;

namespace Humans.Base.Extensions;

/// <summary>
/// Warns when a switch (or dictionary lookup) falls through to its default case. A default
/// is almost always "a value I did not know about" — a new enum member nobody wrote a branch
/// for — so this turns a silent, wrong-but-plausible fallback into a log line naming the
/// enum type, the value, and the call site. See memory/architecture/switch-default-warn.md.
/// </summary>
public static partial class LoggerSwitchExtensions
{
    public static void SwitchDefaultWarn(
        this ILogger logger, object value, [CallerMemberName] string site = "")
        => LogSwitchDefault(logger, site, value.GetType().Name, value.ToString() ?? "null");

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{Site} fell through to default for unhandled {EnumType} value '{Value}'")]
    private static partial void LogSwitchDefault(ILogger logger, string site, string enumType, string value);
}
