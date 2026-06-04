using System.Text.Json;

namespace Humans.Web.Models.Survey;

/// <summary>
/// Get/set the per-token <see cref="SurveyWizardState"/> on <see cref="ISession"/> as a JSON string.
/// Keeps HTTP/session types in the Web layer. Keyed per token so multiple invite links don't collide.
/// </summary>
internal static class SurveyWizardSession
{
    private static string Key(string token) => $"survey-wizard:{token}";

    public static SurveyWizardState? Load(ISession session, string token)
    {
        var json = session.GetString(Key(token));
        return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<SurveyWizardState>(json);
    }

    public static void Save(ISession session, string token, SurveyWizardState state)
        => session.SetString(Key(token), JsonSerializer.Serialize(state));

    public static void Clear(ISession session, string token)
        => session.Remove(Key(token));
}
