using System.Text.Json;
using Humans.Application.Interfaces.Surveys;

namespace Humans.Web.Models.Survey;

/// <summary>
/// Get/set the per-key <see cref="SurveyWizardState"/> on <see cref="ISession"/> as a JSON string.
/// Keeps HTTP/session types in the Web layer. The invited path keys per token
/// (<c>survey-wizard:{token}</c>); the public path keys per slug (<c>survey-wizard:slug:{slug}</c>) so
/// the two namespaces never collide.
/// </summary>
internal static class SurveyWizardSession
{
    private static string Key(string token) => $"survey-wizard:{token}";

    private static string SlugKey(string slug) => $"survey-wizard:slug:{slug}";

    public static SurveyWizardState? Load(ISession session, string token)
        => LoadByKey(session, Key(token));

    public static void Save(ISession session, string token, SurveyWizardState state)
        => session.SetString(Key(token), JsonSerializer.Serialize(state));

    public static void Clear(ISession session, string token)
        => session.Remove(Key(token));

    public static SurveyWizardState? LoadBySlug(ISession session, string slug)
        => LoadByKey(session, SlugKey(slug));

    public static void SaveBySlug(ISession session, string slug, SurveyWizardState state)
        => session.SetString(SlugKey(slug), JsonSerializer.Serialize(state));

    public static void ClearBySlug(ISession session, string slug)
        => session.Remove(SlugKey(slug));

    private static SurveyWizardState? LoadByKey(ISession session, string key)
    {
        var json = session.GetString(key);
        return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<SurveyWizardState>(json);
    }
}
