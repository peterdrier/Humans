using Humans.Base.Interfaces;
using Humans.GoogleIntegration.Contracts;
using Humans.Surveys.Domain;

namespace Humans.Surveys.Services;

internal sealed class SurveyTranslationFiller(IGoogleTranslationService translation) : IApplicationService
{
    internal async Task<(SurveyEditInput Input, int Filled)> FillAsync(
        SurveyEditInput e, IReadOnlyList<string> targetCultures, CancellationToken ct)
    {
        var source = e.DefaultCulture;

        // Mutable copies of every authored LocalizedText, kept alongside what they rebuild into.
        var title = Copy(e.Title);
        var intro = Copy(e.Intro);
        var thankYou = Copy(e.ThankYou);
        var invitationEmailSubject = Copy(e.InvitationEmailSubject);
        var invitationEmailMessage = Copy(e.InvitationEmailMessage);
        var questions = e.Questions.Select(q => new
        {
            Question = q,
            Prompt = Copy(q.Prompt),
            Help = Copy(q.HelpText),
            MinLabel = Copy(q.RatingMinLabel),
            MaxLabel = Copy(q.RatingMaxLabel),
            Options = q.Options.Select(o => new { Option = o, Label = Copy(o.Label) }).ToList(),
            GridRows = (q.GridRows ?? []).Select(row => new { Row = row, Label = Copy(row.Label) }).ToList(),
            InformationImages = (q.InformationImages ?? []).Select(image => new
            {
                Image = image,
                Label = Copy(image.Label),
                AltText = Copy(image.AltText),
            }).ToList(),
        }).ToList();

        var allTexts = new List<Dictionary<string, string>>
        {
            title,
            intro,
            thankYou,
            invitationEmailSubject,
            invitationEmailMessage,
        };
        foreach (var q in questions)
        {
            allTexts.AddRange([q.Prompt, q.Help, q.MinLabel, q.MaxLabel]);
            allTexts.AddRange(q.Options.Select(o => o.Label));
            allTexts.AddRange(q.GridRows.Select(row => row.Label));
            allTexts.AddRange(q.InformationImages.Select(image => image.Label));
            allTexts.AddRange(q.InformationImages.Select(image => image.AltText));
        }

        // One batched call per target culture; only fills blanks — never overwrites authored text.
        var filled = 0;
        foreach (var target in targetCultures.Where(t => !string.Equals(t, source, StringComparison.OrdinalIgnoreCase)))
        {
            var pending = allTexts.Where(d => HasText(d, source) && !HasText(d, target)).ToList();
            if (pending.Count == 0) continue;

            var translated = await translation.TranslateAsync(
                pending.Select(d => d[source]).ToList(), source, target, ct);
            for (var i = 0; i < pending.Count; i++)
            {
                pending[i][target] = translated[i];
            }

            filled += pending.Count;
        }

        if (filled == 0) return (e, 0);

        var input = new SurveyEditInput(
            new LocalizedText(title), new LocalizedText(intro), new LocalizedText(thankYou),
            new LocalizedText(invitationEmailSubject), new LocalizedText(invitationEmailMessage),
            e.DefaultCulture, e.AllowAnonymous, e.OpensAt, e.ClosesAt,
            e.AudienceType, e.AudienceTeamId, e.AudienceLoggedInSince, e.PublicSlug,
            questions.Select(q => q.Question with
            {
                Prompt = new LocalizedText(q.Prompt),
                HelpText = new LocalizedText(q.Help),
                RatingMinLabel = new LocalizedText(q.MinLabel),
                RatingMaxLabel = new LocalizedText(q.MaxLabel),
                Options = q.Options.Select(o => o.Option with { Label = new LocalizedText(o.Label) }).ToList(),
                GridRows = q.GridRows.Select(row => row.Row with { Label = new LocalizedText(row.Label) }).ToList(),
                InformationImages = q.InformationImages.Select(image => image.Image with
                {
                    Label = new LocalizedText(image.Label),
                    AltText = new LocalizedText(image.AltText),
                }).ToList(),
            }).ToList(),
            e.IsAsociadoVote);

        return (input, filled);

        static Dictionary<string, string> Copy(LocalizedText text)
            => new(text.Values, StringComparer.Ordinal);

        static bool HasText(Dictionary<string, string> values, string culture)
            => values.TryGetValue(culture, out var v) && !string.IsNullOrWhiteSpace(v);
    }
}
