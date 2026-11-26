namespace Humans.Web.Services;

/// <summary>
/// In-memory tally of interactive sign-ins by method since the process started, for the
/// admin dashboard's dial. Deliberately not persisted — a restart zeroes it. Counts
/// sign-in events, not people; the gate terminal and dev logins are not counted.
/// </summary>
public sealed class SignInMethodCounter
{
    private int _google;
    private int _signInLink;

    public int Google => Volatile.Read(ref _google);
    public int SignInLink => Volatile.Read(ref _signInLink);

    public void RecordGoogle() => Interlocked.Increment(ref _google);
    public void RecordSignInLink() => Interlocked.Increment(ref _signInLink);
}
