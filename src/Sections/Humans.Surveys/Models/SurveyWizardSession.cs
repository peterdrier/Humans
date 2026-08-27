using Humans.Surveys.Contracts;
using System.Text.Json;
using System.Text.Json.Serialization;
using Humans.Surveys.Services;

namespace Humans.Surveys.Models;

/// <summary>What the thank-you page needs once the wizard state is gone: which survey, in which language.</summary>
[method: JsonConstructor]
internal sealed record SurveyCompletion(Guid SurveyId, string Culture);

/// <summary>
/// Get/set the per-key <see cref="SurveyWizardState"/> on <see cref="ISession"/> as a JSON string.
/// Keeps HTTP/session types in the Web layer. The invited path keys per token
/// (<c>survey-wizard:{token}</c>); the public path keys per slug (<c>survey-wizard:slug:{slug}</c>) so
/// the two namespaces never collide. A third namespace (<c>survey-thankyou:…</c>) outlives the wizard
/// state and carries <see cref="SurveyCompletion"/> to the thank-you page.
/// </summary>
internal static class SurveyWizardSession
{
    private static string Key(string token) => $"survey-wizard:{token}";

    private static string SlugKey(string slug) => $"survey-wizard:slug:{slug}";

    private static string DoneKey(string token) => $"survey-thankyou:{token}";

    private static string DoneSlugKey(string slug) => $"survey-thankyou:slug:{slug}";

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

    public static SurveyCompletion? LoadCompleted(ISession session, string token)
        => LoadCompletedByKey(session, DoneKey(token));

    public static void SaveCompleted(ISession session, string token, SurveyCompletion completion)
        => session.SetString(DoneKey(token), JsonSerializer.Serialize(completion));

    public static SurveyCompletion? LoadCompletedBySlug(ISession session, string slug)
        => LoadCompletedByKey(session, DoneSlugKey(slug));

    public static void SaveCompletedBySlug(ISession session, string slug, SurveyCompletion completion)
        => session.SetString(DoneSlugKey(slug), JsonSerializer.Serialize(completion));

    private static SurveyWizardState? LoadByKey(ISession session, string key)
    {
        var json = session.GetString(key);
        return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<SurveyWizardState>(json);
    }

    private static SurveyCompletion? LoadCompletedByKey(ISession session, string key)
    {
        var json = session.GetString(key);
        return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<SurveyCompletion>(json);
    }
}
