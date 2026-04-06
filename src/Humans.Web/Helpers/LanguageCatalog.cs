namespace Humans.Web.Helpers;

/// <summary>
/// ISO 639-1 language code catalog with English display names.
/// Contains the most commonly spoken languages relevant to the community.
/// </summary>
public static class LanguageCatalog
{
    /// <summary>
    /// Sorted dictionary of ISO 639-1 code to English language name.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Languages =
        new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["af"] = "Afrikaans",
            ["am"] = "Amharic",
            ["ar"] = "Arabic",
            ["az"] = "Azerbaijani",
            ["be"] = "Belarusian",
            ["bg"] = "Bulgarian",
            ["bn"] = "Bengali",
            ["bs"] = "Bosnian",
            ["ca"] = "Catalan",
            ["cs"] = "Czech",
            ["cy"] = "Welsh",
            ["da"] = "Danish",
            ["de"] = "German",
            ["el"] = "Greek",
            ["en"] = "English",
            ["eo"] = "Esperanto",
            ["es"] = "Spanish",
            ["et"] = "Estonian",
            ["eu"] = "Basque",
            ["fa"] = "Persian",
            ["fi"] = "Finnish",
            ["fr"] = "French",
            ["ga"] = "Irish",
            ["gl"] = "Galician",
            ["gu"] = "Gujarati",
            ["ha"] = "Hausa",
            ["he"] = "Hebrew",
            ["hi"] = "Hindi",
            ["hr"] = "Croatian",
            ["hu"] = "Hungarian",
            ["hy"] = "Armenian",
            ["id"] = "Indonesian",
            ["is"] = "Icelandic",
            ["it"] = "Italian",
            ["ja"] = "Japanese",
            ["ka"] = "Georgian",
            ["kk"] = "Kazakh",
            ["km"] = "Khmer",
            ["kn"] = "Kannada",
            ["ko"] = "Korean",
            ["ku"] = "Kurdish",
            ["la"] = "Latin",
            ["lt"] = "Lithuanian",
            ["lv"] = "Latvian",
            ["mk"] = "Macedonian",
            ["ml"] = "Malayalam",
            ["mn"] = "Mongolian",
            ["mr"] = "Marathi",
            ["ms"] = "Malay",
            ["mt"] = "Maltese",
            ["my"] = "Burmese",
            ["ne"] = "Nepali",
            ["nl"] = "Dutch",
            ["no"] = "Norwegian",
            ["pa"] = "Punjabi",
            ["pl"] = "Polish",
            ["ps"] = "Pashto",
            ["pt"] = "Portuguese",
            ["ro"] = "Romanian",
            ["ru"] = "Russian",
            ["si"] = "Sinhala",
            ["sk"] = "Slovak",
            ["sl"] = "Slovenian",
            ["so"] = "Somali",
            ["sq"] = "Albanian",
            ["sr"] = "Serbian",
            ["sv"] = "Swedish",
            ["sw"] = "Swahili",
            ["ta"] = "Tamil",
            ["te"] = "Telugu",
            ["th"] = "Thai",
            ["tl"] = "Filipino",
            ["tr"] = "Turkish",
            ["uk"] = "Ukrainian",
            ["ur"] = "Urdu",
            ["uz"] = "Uzbek",
            ["vi"] = "Vietnamese",
            ["zh"] = "Chinese",
            ["zu"] = "Zulu"
        };

    /// <summary>
    /// Gets the display name for a language code, or the code itself if not found.
    /// </summary>
    public static string GetDisplayName(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
            return string.Empty;

        return Languages.TryGetValue(languageCode, out var name) ? name : languageCode;
    }
}
