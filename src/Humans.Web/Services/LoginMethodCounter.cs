namespace Humans.Web.Services;

/// <summary>
/// In-memory tally of interactive sign-ins by method since the process started, for the
/// admin dashboard's dial. Deliberately not persisted — a restart zeroes it. Counts
/// sign-in events, not people; the gate terminal and dev logins are not counted.
/// </summary>
public sealed class LoginMethodCounter
{
    private int _google;
    private int _magicLink;

    public int Google => Volatile.Read(ref _google);
    public int MagicLink => Volatile.Read(ref _magicLink);

    public void RecordGoogle() => Interlocked.Increment(ref _google);
    public void RecordMagicLink() => Interlocked.Increment(ref _magicLink);
}
