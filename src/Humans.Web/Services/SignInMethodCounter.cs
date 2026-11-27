using System.Collections.Concurrent;

namespace Humans.Web.Services;

/// <summary>
/// In-memory tally of interactive sign-ins by method (each external provider's scheme name, or the email link)
/// since the process started, for the admin dashboard's dial. Deliberately not persisted — a restart zeroes it.
/// Counts sign-in events, not people; the gate terminal and dev logins are not counted.
/// </summary>
public sealed class SignInMethodCounter
{
    private readonly ConcurrentDictionary<string, int> _providers = new(StringComparer.Ordinal);
    private int _signInLink;

    public int SignInLink => Volatile.Read(ref _signInLink);

    public IReadOnlyDictionary<string, int> Providers => new Dictionary<string, int>(_providers, StringComparer.Ordinal);

    public void RecordProvider(string provider) => _providers.AddOrUpdate(provider, 1, (_, count) => count + 1);
    public void RecordSignInLink() => Interlocked.Increment(ref _signInLink);
}
