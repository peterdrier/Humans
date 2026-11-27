namespace Humans.Backdoor.Models;

/// <summary>The approval page: who is asking and where the sign-in goes back to.</summary>
/// <param name="ClientName">The app asking, such as Claude.</param>
/// <param name="ReturnHost">Where the sign-in goes back to, shown so a look-alike request is spotted.</param>
/// <param name="Parameters">The OAuth request, carried through the form unchanged.</param>
internal sealed record BackdoorAuthorizeViewModel(
    string ClientName, string ReturnHost, IReadOnlyList<KeyValuePair<string, string>> Parameters);
