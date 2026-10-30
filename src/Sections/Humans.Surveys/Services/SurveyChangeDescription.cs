using Humans.Surveys.Contracts;
using Humans.Surveys.Domain;

namespace Humans.Surveys.Services;

internal static class SurveyChangeDescription
{
    /// <summary>
    /// Short field list for the SurveyUpdated audit entry — names only, no before/after values,
    /// except the governance-relevant audience type and slug transitions. Question edits are
    /// collapsed to counts; deep content (grid rows, images, branching) is not diffed, so an
    /// update touching only those falls back to the bare "Updated survey".
    /// </summary>
    public static string DescribeSurveyChanges(Survey existing, Survey updated)
    {
        var changes = new List<string>();
        if (!existing.Title.Equals(updated.Title)) changes.Add("title");
        if (!existing.Intro.Equals(updated.Intro)) changes.Add("intro");
        if (!existing.ThankYou.Equals(updated.ThankYou)) changes.Add("thank-you text");
        if (!existing.InvitationEmailSubject.Equals(updated.InvitationEmailSubject)) changes.Add("invitation subject");
        if (!existing.InvitationEmailMessage.Equals(updated.InvitationEmailMessage)) changes.Add("invitation message");
        if (!string.Equals(existing.DefaultCulture, updated.DefaultCulture, StringComparison.OrdinalIgnoreCase))
            changes.Add($"default culture ({existing.DefaultCulture} → {updated.DefaultCulture})");
        if (existing.AllowAnonymous != updated.AllowAnonymous)
            changes.Add(updated.AllowAnonymous ? "anonymous responses enabled" : "anonymous responses disabled");
        if ((existing.IsAsociadoVote == true) != (updated.IsAsociadoVote == true))
            changes.Add(updated.IsAsociadoVote == true ? "Asociado vote enabled" : "Asociado vote disabled");
        if (existing.OpensAt != updated.OpensAt) changes.Add("opens-at");
        if (existing.ClosesAt != updated.ClosesAt) changes.Add("closes-at");
        if (existing.AudienceType != updated.AudienceType)
            changes.Add($"audience ({existing.AudienceType?.ToString() ?? "none"} → {updated.AudienceType?.ToString() ?? "none"})");
        else if (existing.AudienceTeamId != updated.AudienceTeamId || existing.AudienceLoggedInSince != updated.AudienceLoggedInSince)
            changes.Add("audience");
        if (!string.Equals(existing.PublicSlug, updated.PublicSlug, StringComparison.Ordinal))
            changes.Add(existing.PublicSlug is null ? "public slug set"
                : updated.PublicSlug is null ? "public slug removed"
                : "public slug changed");

        var oldQuestions = existing.Questions.ToDictionary(q => q.Id);
        var newQuestions = updated.Questions.ToDictionary(q => q.Id);
        foreach (var question in newQuestions.Values.Where(q => q.Type == SurveyQuestionType.RankedChoice))
        {
            if (!oldQuestions.TryGetValue(question.Id, out var oldQuestion)) continue;
            var oldUnavailable = (oldQuestion.RankedUnavailableOptionValues ?? [])
                .ToHashSet(StringComparer.Ordinal);
            var newUnavailable = (question.RankedUnavailableOptionValues ?? [])
                .ToHashSet(StringComparer.Ordinal);
            var addedUnavailable = newUnavailable.Except(oldUnavailable, StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            var restored = oldUnavailable.Except(newUnavailable, StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            if (addedUnavailable.Count > 0)
                changes.Add($"ranked options unavailable ({string.Join(", ", addedUnavailable)})");
            if (restored.Count > 0)
                changes.Add($"ranked options restored ({string.Join(", ", restored)})");
        }

        var added = newQuestions.Keys.Count(id => !oldQuestions.ContainsKey(id));
        var removed = oldQuestions.Keys.Count(id => !newQuestions.ContainsKey(id));
        var edited = newQuestions.Values.Count(q =>
            oldQuestions.TryGetValue(q.Id, out var old) && QuestionChanged(old, q));
        if (added > 0) changes.Add($"{added} question(s) added");
        if (removed > 0) changes.Add($"{removed} question(s) removed");
        if (edited > 0) changes.Add($"{edited} question(s) edited");

        return changes.Count == 0 ? "Updated survey" : $"Updated survey: {string.Join(", ", changes)}";
    }

    private static bool QuestionChanged(SurveyQuestion old, SurveyQuestion updated) =>
        old.Type != updated.Type
        || old.PageNumber != updated.PageNumber
        || old.Order != updated.Order
        || old.IsRequired != updated.IsRequired
        || !old.Prompt.Equals(updated.Prompt)
        || !old.HelpText.Equals(updated.HelpText)
        || old.RatingMin != updated.RatingMin
        || old.RatingMax != updated.RatingMax
        || !old.RatingMinLabel.Equals(updated.RatingMinLabel)
        || !old.RatingMaxLabel.Equals(updated.RatingMaxLabel)
        || old.GridSelectionMode != updated.GridSelectionMode
        || old.RankedSettings != updated.RankedSettings
        || !(old.RankedUnavailableOptionValues ?? [])
            .SequenceEqual(updated.RankedUnavailableOptionValues ?? [], StringComparer.Ordinal)
        || !OptionsEqual(old.Options, updated.Options);

    private static bool OptionsEqual(ICollection<SurveyQuestionOption> old, ICollection<SurveyQuestionOption> updated) =>
        old.Count == updated.Count
        && old.OrderBy(o => o.Order).Zip(updated.OrderBy(o => o.Order))
            .All(pair => pair.First.Order == pair.Second.Order
                && string.Equals(pair.First.Value, pair.Second.Value, StringComparison.Ordinal)
                && pair.First.Label.Equals(pair.Second.Label));
}
