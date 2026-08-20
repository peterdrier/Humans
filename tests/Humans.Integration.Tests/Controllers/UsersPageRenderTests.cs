using System.Net;
using AwesomeAssertions;
using Humans.Camps.Contracts;
using Humans.Base.Enums;
using Humans.Events.Contracts;
using Humans.Events.Domain;
using Humans.Events.Services;
using Humans.Integration.Tests.Infrastructure;
using Humans.Tickets.Contracts;
using Humans.Tickets.Data;
using Humans.Tickets.Domain;
using Humans.Tickets.Services;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders the Users/Profile pages through the real app
/// (nobodies-collective/Humans#1054). Every other section has this guard; the one
/// carrying the most-visited authenticated page did not.
/// </summary>
/// <remarks>
/// <para>
/// The failure this exists to catch is a <b>200 with a hollow page</b>. During the G5
/// Users move, <c>Profile/Index</c> rendered as a shell: the moved views could no
/// longer bind the <c>&lt;vc:…&gt;</c> tag helpers, so five view components shipped as
/// literal markup the browser drops. The build was green, the routes were
/// byte-identical, and 5,480 tests passed. Status-code assertions cannot see it.
/// </para>
/// <para>
/// So the assertions here are on card <b>content</b>. Four of the six cards on the
/// page — my-camps, events-card, ticket-holdings and shift-signups — return
/// <c>Content(string.Empty)</c> when the human has no rows, which means the seeded
/// dev personas alone cannot tell an auto-hidden card apart from a broken one.
/// <see cref="SeedProfileCardDataAsync"/> therefore gives the persona a ticket, a
/// barrio membership and an approved personal event before the page is fetched.
/// </para>
/// <para>
/// Each seed goes through the owning section and then clears that section's §15
/// decorator, because all three are Singletons that warmed at host start — before
/// the test body — and a bare context write is invisible to every read afterwards
/// (Consent's rule). Events has no blanket invalidator, so its event is created
/// through <c>IEventService</c>'s own submit + moderate path, which owns the
/// surgical cache reload.
/// </para>
/// </remarks>
public class UsersPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private const string TicketHolderName = "Integration Ticket Holder";
    private const string CampName = "Integration Test Barrio";
    private const string EventTitle = "Integration Test Sunrise Set";
    private const string EventCategoryName = "Integration Music";
    private const string EventVenueName = "Integration Main Stage";

    /// <summary>
    /// Every resource prefix the Users/Profile views read. A key the Users carve
    /// misses, or a call site left on the wrong localizer, renders the raw key — in
    /// every language, with no error and no failing build.
    /// </summary>
    /// <remarks>
    /// Every prefix in <c>UsersResource</c> after the carve, plus the shared ones these
    /// pages still read. <c>Admin_</c> is deliberately absent: <c>Admin_SortBy</c>
    /// (<c>Views/UsersAdmin/AdminList.cshtml:78</c>) exists in no resource file and has
    /// been rendering raw since before the carve — adding the prefix here would fail on
    /// that pre-existing bug rather than on a regression.
    /// </remarks>
    private static readonly string[] ProfilePrefixes =
    [
        "Profile_", "ProfileEdit_", "ProfilePrivacy_", "ProfileCard_", "CommPrefs_",
        "Emails_", "EmailGrid_", "Outbox_", "LinkedAccounts_", "VerifyEmail_",
        "SendMessage_", "Unsub_", "AccountStatus_", "AccountDeletion_",
        "AdminHuman_", "AdminHumans_", "AdminHumanDetail_", "AdminDetail_",
        "AdminAddRole_", "AdminRoles_", "Enum_",
        "Dashboard_", "Common_", "Nav_",
    ];

    /// <summary>The Users pages that are not <c>Profile/Me</c>. No fixture — each has a no-data branch that still renders its own chrome.</summary>
    private static (string Url, string Copy)[] SecondaryPages =>
    [
        ("/Profile/Me/Edit", "Edit Profile"),                                // ProfileEdit_Title
        ("/Profile/Me/Emails", "Email Addresses"),                           // Emails_Title
        ("/Profile/Me/Privacy", "My Data"),                                  // ProfilePrivacy_Title
        ("/Profile/Me/DietaryMedical", "Dietary"),                           // Profile_DietaryMedical_PageTitle
        ("/Profile/Me/CommunicationPreferences", "Communication Preferences"),// CommPrefs_Title
        ("/Profile/Me/Outbox", "My Emails"),                                 // Profile_MyEmails
    ];

    [HumansFact(Timeout = 180000)]
    public async Task The_profile_page_renders_every_card_it_invokes()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var campName = await SeedProfileCardDataAsync(userId, ct);

        var response = await Client.GetAsync("/Profile/Me", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        // The page's own chrome, and the two cards that render regardless of data.
        html.Should().Contain("My Profile", "the page must render its own title");
        html.Should().Contain("sectionHelp-Profile",
            "<vc:access-matrix section=\"Profile\" /> must bind through @addTagHelper *, Humans.Interfaces " +
            "(nobodies-collective/Humans#1056) — an unbound <vc:> ships inert with a green build");
        html.Should().Contain("Profile Information", "the profile-card must render");
        html.Should().Contain("Quick Actions", "the quick-actions panel must render");

        // The four data-bearing cards, each against the row seeded for it. This is
        // the assertion the G5 move needed: a hollow page satisfies every other one.
        html.Should().Contain("ticket-stub-name", "the ticket-holdings card must render");
        html.Should().Contain(TicketHolderName, "the ticket-holdings card must render its stub");
        html.Should().Contain(campName, "the my-camps card must render the seeded membership");
        html.Should().Contain(EventTitle, "the events-card must render the seeded event");

        AssertRenderedCleanly(html, "GET /Profile/Me");
    }

    [HumansFact(Timeout = 180000)]
    public async Task Every_other_users_page_renders_its_own_copy()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        foreach (var (url, copy) in SecondaryPages)
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);
            html.Should().Contain(copy, $"GET {url} must render its own copy");
            AssertRenderedCleanly(html, $"GET {url}");
        }
    }

    /// <summary>
    /// The two person-search surfaces after nobodies-collective/Humans#1062 replaced the
    /// shared <c>_HumanSearchResults</c> partial: <c>/Profile/Search</c> renders one
    /// <c>&lt;vc:user-search-result&gt;</c> per hit, <c>/Users/Admin</c> renders its own
    /// admin-directory rows.
    /// </summary>
    /// <remarks>
    /// Both assertions are on content only the row itself can have produced. A
    /// <c>NotContain("&lt;vc:")</c> check passes vacuously on an empty-state page, so the
    /// seeded persona's own name and the match badge are what prove the element bound —
    /// delete <c>@addTagHelper *, Humans.Users</c> from the page's <c>_ViewImports</c> and
    /// the row ships as inert literal markup on a green 200.
    /// </remarks>
    [HumansFact(Timeout = 120000)]
    public async Task Both_person_search_surfaces_render_their_result_rows()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var publicSearch = await Client.GetAsync("/Profile/Search?q=Dev", ct);
        publicSearch.StatusCode.Should().Be(HttpStatusCode.OK);
        var publicHtml = await publicSearch.Content.ReadAsStringAsync(ct);

        publicHtml.Should().Contain("Dev Admin",
            because: "<vc:user-search-result> must resolve the hit and render the human");
        publicHtml.Should().Contain("Matched in",
            because: "Search_MatchedIn is written by the component's own markup and nothing else "
                   + "on this page emits it");
        AssertRenderedCleanly(publicHtml, "GET /Profile/Search?q=Dev");

        var adminList = await Client.GetAsync("/Users/Admin", ct);
        adminList.StatusCode.Should().Be(HttpStatusCode.OK);
        var adminHtml = await adminList.Content.ReadAsStringAsync(ct);

        adminHtml.Should().Contain("Dev Admin",
            because: "the admin directory rows must render their humans");
        adminHtml.Should().Contain("Joined",
            because: "Admin_Joined is on the row's meta line, which only the row markup writes");
        adminHtml.Should().Contain($"/Users/Admin/{userId}",
            because: "the detail link used to come from a Func the controller passed the builder; "
                   + "it is now asp-action=\"AdminDetail\" and must resolve to the same route");
        AssertRenderedCleanly(adminHtml, "GET /Users/Admin");
    }

    [HumansFact(Timeout = 120000)]
    public async Task A_volunteer_cannot_reach_another_humans_admin_email_page()
    {
        // The section's negative access rule. Cookie authentication redirects an
        // authenticated-but-unauthorized request to Program.cs's AccessDeniedPath
        // rather than returning a bare 403 (Cantina's finding).
        var ct = Xunit.TestContext.Current.CancellationToken;
        var otherUserId = await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        using var volunteerClient = Factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });
        await Factory.SignInAsFullyOnboardedAsync(volunteerClient, DevPersona.Volunteer);

        var response = await volunteerClient.GetAsync($"/Profile/{otherUserId}/Admin/Emails", ct);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location?.AbsolutePath.Should().Be("/Account/AccessDenied",
            because: "another human's admin email page must be denied, not sent to sign in");
    }

    /// <summary>
    /// The carve's real risk: <c>UsersResource.es</c> and friends must ship as satellite
    /// assemblies of <c>Humans.Users</c> and be found from there. A resx that builds but
    /// whose manifest name does not match the marker type's namespace falls back to
    /// English silently — same 200, same layout, no error anywhere
    /// (nobodies-collective/Humans#1050).
    /// </summary>
    [HumansFact(Timeout = 180000)]
    public async Task Users_pages_render_in_spanish_from_the_sections_satellite_assemblies()
    {
        // Razor's default HtmlEncoder escapes non-ASCII to numeric entities, so every
        // assertion below is on an ASCII-only run of the Spanish copy.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        // Accept-Language never reaches a signed-in page — Program.cs's first culture
        // provider returns the user's PreferredLanguage and short-circuits the chain.
        // Switch the way the UI does.
        var switcherPage = await (await Client.GetAsync("/Profile/Me", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token!),
                new KeyValuePair<string, string>("culture", "es"),
            ]), ct);

        var profile = await Client.GetAsync("/Profile/Me", ct);
        profile.StatusCode.Should().Be(HttpStatusCode.OK);
        var profileHtml = await profile.Content.ReadAsStringAsync(ct);

        profileHtml.Should().Contain("Mi perfil");                  // Profile_Title
        profileHtml.Should().NotContain("Profile Information",      // Profile_Information
            because: "an English string here means the .es satellite was not found");
        AssertRenderedCleanly(profileHtml, "GET /Profile/Me (es)");

        // One page per carved prefix family that has its own title key, so a satellite
        // that resolves for Profile_ but not for its neighbours still fails.
        foreach (var (url, copy) in new[]
                 {
                     ("/Profile/Me/Edit", "Editar perfil"),          // ProfileEdit_Title
                     ("/Profile/Me/Emails", "Direcciones de correo"),// Emails_Title
                     ("/Profile/Me/Privacy", "Mis datos"),           // ProfilePrivacy_Title
                 })
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);
            html.Should().Contain(copy, $"GET {url} must render its Spanish copy");
            AssertRenderedCleanly(html, $"GET {url} (es)");
        }
    }

    private static string? ExtractAntiForgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]{0,200}value=\"(?<token>[^\"]+)\"",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(5));

        return match.Success ? match.Groups["token"].Value : null;
    }

    /// <summary>
    /// Gives the persona one row in each of the three sections whose profile cards
    /// auto-hide when empty. Returns the seeded barrio's name, which is what the
    /// my-camps card renders.
    /// </summary>
    private async Task<string> SeedProfileCardDataAsync(Guid userId, CancellationToken ct)
    {
        await SeedTicketAsync(userId, ct);
        var campName = await SeedCampMembershipAsync(userId, ct);
        await SeedApprovedPersonalEventAsync(userId, ct);
        return campName;
    }

    private async Task SeedTicketAsync(Guid userId, CancellationToken ct)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TicketsDbContext>();

            var orderId = Guid.NewGuid();
            db.TicketOrders.Add(new TicketOrder
            {
                Id = orderId,
                VendorOrderId = "or_integration_1054",
                BuyerName = TicketHolderName,
                BuyerEmail = "dev-admin@localhost",
                MatchedUserId = userId,
                TotalAmount = 120m,
                Currency = "EUR",
                PaymentStatus = TicketPaymentStatus.Paid,
                VendorEventId = "ev_integration_1054",
                PurchasedAt = now - Duration.FromDays(30),
                SyncedAt = now,
            });
            db.TicketAttendees.Add(new TicketAttendee
            {
                Id = Guid.NewGuid(),
                TicketOrderId = orderId,
                VendorTicketId = "ti_integration_1054",
                AttendeeName = TicketHolderName,
                AttendeeEmail = "dev-admin@localhost",
                MatchedUserId = userId,
                TicketTypeName = "Full Week",
                Price = 120m,
                Status = TicketAttendeeStatus.Valid,
                VendorEventId = "ev_integration_1054",
                SyncedAt = now,
            });

            await db.SaveChangesAsync(ct);
        }

        // CachingTicketQueryService warmed its per-user holdings at host start.
        Factory.Services.GetRequiredService<ITicketCacheInvalidator>().InvalidateAfterContactImport();
    }

    private async Task<string> SeedCampMembershipAsync(Guid userId, CancellationToken ct)
    {
        // The test database is migrated and nothing else — there are no barrios to
        // join. Built through ICampSeeding rather than CampsDbContext because
        // CachingCampService warmed at host start and only its own write verbs
        // refresh it; the sequence is DevPersonaSeeder's.
        using var scope = Factory.Services.CreateScope();
        var camps = scope.ServiceProvider.GetRequiredService<ICampServiceRead>();
        var seeding = scope.ServiceProvider.GetRequiredService<ICampSeeding>();
        var roleSeeding = scope.ServiceProvider.GetRequiredService<ICampRoleSeeding>();

        var year = (await camps.GetSettingsAsync(ct)).PublicYear;

        var campId = await seeding.CreateCampForSeedAsync(
            userId,
            CampName,
            "dev-integration-barrio@localhost",
            "+34 600 000 000",
            isSwissCamp: false,
            timesAtNowhere: 0,
            new CampSeasonData(
                BlurbLong: "Seeded by the Users page-render guard.",
                BlurbShort: "Seeded barrio.",
                Languages: "English",
                AcceptingMembers: YesNoMaybe.Yes,
                KidsWelcome: YesNoMaybe.No,
                KidsVisiting: KidsVisitingPolicy.DaytimeOnly,
                KidsAreaDescription: null,
                HasPerformanceSpace: PerformanceSpaceStatus.No,
                PerformanceTypes: string.Empty,
                Vibes: [],
                AdultPlayspace: AdultPlayspacePolicy.No,
                MemberCount: 1,
                SpaceRequirement: null,
                SoundZone: null,
                ElectricalGrid: null),
            year,
            ct);

        var camp = (await camps.GetCampsForYearAsync(year, ct)).Single(c => c.Id == campId);
        var season = camp.Seasons.Single(s => s.Year == year);
        await seeding.ApproveSeasonAsync(season.Id, userId, "Integration seed", ct);

        // The Camp Lead definition only exists after the system roles are seeded,
        // and the add-member verb is the one that writes an Active CampMember.
        await roleSeeding.SeedSystemRolesAsync(userId, ct);
        var leadDefinitionId = await roleSeeding.GetDefinitionIdBySlugAsync(CampSeedRoles.CampLeadSlug, ct);
        leadDefinitionId.Should().NotBeNull("the Camp Lead role definition must exist after seeding system roles");
        await seeding.AddMemberAndAssignRoleInActiveSeasonAsync(
            camp.Id, leadDefinitionId!.Value, userId, userId, ct);

        return CampName;
    }

    private async Task SeedApprovedPersonalEventAsync(Guid userId, CancellationToken ct)
    {
        // Written through the section's own service rather than the context: the
        // Events decorator has no blanket invalidator, and its approved-event
        // projection is reloaded by the submit + moderate path itself.
        using var scope = Factory.Services.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventService>();
        var now = SystemClock.Instance.GetCurrentInstant();

        var category = new EventCategory
        {
            Id = Guid.NewGuid(),
            Name = EventCategoryName,
            Slug = "integration-music",
            DisplayOrder = 900,
            IsActive = true,
        };
        await events.CreateCategoryAsync(category, ct);

        var venue = new EventVenue
        {
            Id = Guid.NewGuid(),
            Name = EventVenueName,
            DisplayOrder = 900,
            IsActive = true,
        };
        await events.CreateVenueAsync(venue, ct);

        var guideEvent = new Event
        {
            Id = Guid.NewGuid(),
            // A personal (non-camp) event: the profile card filters to CampId is null.
            CampId = null,
            GuideSharedVenueId = venue.Id,
            SubmitterUserId = userId,
            CategoryId = category.Id,
            Title = EventTitle,
            Description = "Seeded by the Users page-render guard.",
            StartAt = now + Duration.FromDays(7),
            DurationMinutes = 90,
            PriorityRank = 1,
            Status = EventStatus.Pending,
            SubmittedAt = now,
            LastUpdatedAt = now,
        };
        await events.SubmitEventAsync(guideEvent, lifecycleActionUrl: null, ct);
        await events.ApplyModerationAsync(
            guideEvent.Id, userId, EventModerationActionType.Approved, reason: null, ct: ct);
    }

    private static void AssertRenderedCleanly(string html, string what)
    {
        // ReSharper rewrites <vc:name> to <name-view-component> when it mistakes the
        // element for a type reference; that also renders as inert markup.
        html.Should().NotContain("-view-component", $"{what} has a rewritten vc tag");

        foreach (var prefix in ProfilePrefixes)
        {
            html.Should().NotContain(prefix, $"{what} rendered a raw {prefix}* resource key");
        }
    }
}
