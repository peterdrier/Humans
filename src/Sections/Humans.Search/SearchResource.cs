namespace Humans.Search;

/// <summary>
/// Marker type for Search's resource set. The <c>.resx</c> files sit beside this file on
/// purpose: the SDK derives the manifest name from the adjacent same-named <c>.cs</c> file's
/// namespace, not from the folder path, so this must stay <c>namespace Humans.Search</c> —
/// <c>Humans.Search.Resources</c> would make every Search string fall back to its raw key at
/// runtime (design §3).
/// </summary>
/// <remarks>
/// Public because the boot localization diagnostic discovers section resource markers via
/// <c>GetExportedTypes()</c>; an internal marker is skipped in silence
/// (G5-SECTION-TEMPLATE.md step 3b).
/// <para>
/// The set is the 12 <c>Search_Filter*</c> / <c>Search_Global*</c> keys — the whole of the
/// <c>/Search</c> page's own copy. Five more keys share the <c>Search_</c> prefix and stayed
/// in <c>SharedResource</c>, because their renderers are elsewhere:
/// <c>Search_Title</c>/<c>Search_Placeholder</c> belong to Shell's <c>/Profile/Search</c>
/// person-search page, <c>Search_NoResults</c>/<c>Search_MatchedIn</c> to
/// <c>Humans.Users</c> — that page's empty state and the section's
/// <c>&lt;vc:user-search-result&gt;</c> — and
/// <c>Search_MinChars</c> is read by <c>/Profile/Search</c> and by this section's own
/// <c>Index.cshtml</c>. Carving that fifth key would split the person-search page's
/// four-key message set to claim one string, so the section binds
/// <c>SharedLocalizer</c> for it (Governance's prefix-vs-key rule, Consent's
/// "would carving split a set?" test answering yes for once).
/// </para>
/// </remarks>
public class SearchResource;
