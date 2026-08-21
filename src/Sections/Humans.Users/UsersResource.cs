namespace Humans.Users;

/// <summary>
/// Marker type for the Users resource set. The <c>.resx</c> files sit beside this file on
/// purpose: the SDK derives the manifest name from the adjacent same-named <c>.cs</c> file's
/// namespace, not from the folder path, so this must stay <c>namespace Humans.Users</c> —
/// <c>Humans.Users.Resources</c> would make every string in the set fall back to its raw key
/// at runtime (design §3).
/// </summary>
/// <remarks>
/// <para>
/// Public because the boot localization diagnostic discovers section resource markers via
/// <c>GetExportedTypes()</c>; an internal marker is skipped in silence (§15 step 3b).
/// </para>
/// <para>
/// The set is 441 keys carved out of <c>SharedResource</c> at
/// nobodies-collective/Humans#1050: whole prefixes <c>Profile_</c> (112 of 118),
/// <c>ProfileEdit_</c> (56 of 60), <c>EmailGrid_</c> (50 of 51), <c>Emails_</c> (39),
/// <c>ProfilePrivacy_</c> (31), <c>AdminHuman_</c> (27 of 30), <c>Unsub_</c> (14),
/// <c>CommPrefs_</c> (14), <c>AccountStatus_</c> (12), <c>AdminAddRole_</c> (12),
/// <c>AdminHumans_</c> (11), <c>SendMessage_</c> (10), <c>LinkedAccounts_</c> (10),
/// <c>AdminRoles_</c> (7), <c>Outbox_</c> (7), <c>ProfileCard_</c> (5),
/// <c>AccountDeletion_</c> (5), <c>VerifyEmail_</c> (3), <c>AdminDetail_</c> (1),
/// <c>AdminHumanDetail_</c> (1), and the 14 <c>Enum_</c> keys of the three enums this
/// section owns on its leaf — <c>ContactFieldType</c>, <c>ContactFieldVisibility</c> and
/// <c>LanguageProficiency</c>.
/// </para>
/// <para>
/// Keys inside a moving prefix that <b>stayed</b> in <c>SharedResource</c>, each because a
/// renderer outside this section reads it (step 3b's carve-by-renderer):
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>Profile_MembershipTier</c>, <c>Profile_MotivationRequired</c> and the four
/// <c>ProfileEdit_Tier*</c> — <c>Humans.Governance</c> renders the same tier-application
/// form on <c>/Governance/Applications/Create</c> and maps the same validation message.
/// </description></item>
/// <item><description>
/// <c>Profile_Location</c> — <c>Humans.Calendar</c>'s event form and its field partial.
/// <c>Profile_Updated</c> — <c>Humans.Shifts</c>' <c>ShiftProfileController</c> sets the
/// same success flash. <c>AdminHuman_Active</c> — Teams' admin team row.
/// <c>AdminHuman_MembershipTier</c> — Governance's board-voting detail and Onboarding's
/// review detail. <c>EmailGrid_LinkFailed</c> — Shell's <c>AccountController</c>.
/// </description></item>
/// <item><description>
/// Three keys whose <b>only</b> renderer is another section, so they were never this
/// section's to take: <c>Profile_BuildStrip_Title</c> (Shifts' volunteer heatmap),
/// <c>Profile_Coordinator</c> (Teams' <c>_RoleBadge</c>) and <c>AdminHuman_Approved</c>
/// (Governance's applications list).
/// </description></item>
/// </list>
/// <para>
/// Whole prefixes this section renders and did <b>not</b> take, because the prefix is a
/// shared vocabulary rather than this section's: <c>Common_</c>, <c>Nav_</c>,
/// <c>Dashboard_</c>, <c>Admin_</c>, <c>Validation_</c>, <c>Application_</c> /
/// <c>ApplicationStatus_</c> (Governance's), <c>Teams_</c> / <c>MyTeams_</c> /
/// <c>TeamDetail_</c> (Teams'), <c>Privacy_</c> (Shell's policy page), and <c>Search_</c>
/// — five keys of which <c>Search_MinChars</c> is co-rendered by <c>Humans.Search</c>, so
/// claiming the other four would split a five-key set. <c>Todo_</c> stays for the same
/// reason Shifts' and Governance's did: the things-to-do list is one vocabulary every
/// section contributes entries to, and all three <c>SectionThingsToDo</c> implementations
/// resolve it through <c>SharedResource</c>.
/// </para>
/// <para>
/// Views read the leftovers through <c>SharedLocalizer</c>, bound beside <c>Localizer</c>
/// in <c>Views/_ViewImports.cshtml</c>; <c>Enum_EmailOutboxStatus_*</c> on
/// <c>Profile/Outbox</c> is <c>Humans.Email</c>'s enum and goes through
/// <c>SharedLocalizer.EnumDisplay</c> for the same reason.
/// <c>UserArchitectureTests.SectionTypesLocalizeThroughTheSectionsOwnResourceSet</c> is
/// what stops a controller quietly keeping <c>IStringLocalizer&lt;SharedResource&gt;</c>
/// after its keys moved here — a failure mode no render test reaches, because
/// controller-resolved copy sits on the validation and error paths (Surveys' finding).
/// </para>
/// <para>
/// <c>Views/Profile/Edit.cshtml</c> keeps <c>IStringLocalizer&lt;ShiftsResource&gt;</c> for
/// the two shift-preference labels on its preferences card: the card is Users' own markup
/// over Users' own form, the copy is Shifts' vocabulary, and the
/// <c>Humans.Users</c> → <c>Humans.Shifts</c> reference that binding needs is required
/// anyway by <c>&lt;vc:shift-signups&gt;</c> on <c>Profile/Index</c> and
/// <c>UsersAdmin/AdminDetail</c>.
/// </para>
/// </remarks>
public class UsersResource;
