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
using Humans.Tickets.Services.Stores;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders the Users/Profile pages through the real app.</summary>
/// <remarks>
/// The failure is a 200 with a hollow page: unbound <c>&lt;vc:…&gt;</c> tags ship as markup the browser drops, on a
/// green build. So the assertions are on card content, and since four cards hide themselves with no rows,
/// <see cref="SeedProfileCardDataAsync"/> seeds one row for each. Each seed goes through its owning section and clears
/// that section's Singleton cache, which warmed before the test body; Events has no blanket invalidator, so its event
/// goes through the submit and moderate path.
/// </remarks>
public class UsersPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private const string TicketHolderName = "Integration Ticket Holder";
    private const string CampName = "Integration Test Barrio";
    private const string EventTitle = "Integration Test Sunrise Set";
    private const string EventCategoryName = "Integration Music";
    private const string EventVenueName = "Integration Main Stage";

    /// <summary>
    /// A mis-bound key renders as its raw name in every language, with no error.
    /// </summary>
    /// <remarks>
    /// <c>Admin_</c> is left out: <c>Admin_SortBy</c> (<c>Views/UsersAdmin/AdminList.cshtml</c>) exists in no resource
    /// file and already renders raw, so the prefix would fail on that existing bug.
    /// </remarks>
    private static readonly string[] ProfilePrefixes =
    [
        "Profile_", "ProfileEdit_", "ProfilePrivacy_", "ProfileCard_", "CommPrefs_",
        "Emails_", "EmailGrid_", "Email_Outbox_", "LinkedAccounts_", "VerifyEmail_",
        "SendMessage_", "Users_Unsubscribe_", "AccountStatus_",
        "AdminHuman_", "AdminHumans_", "AdminHumanDetail_", "AdminDetail_",
        "Enum_",
        "Dashboard_", "Governance_", "Common_", "Nav_",
    ];

    /// <summary>No fixture: each has a no-data branch that still renders its own chrome.</summary>
    private static (string Url, string Copy)[] SecondaryPages =>
    [
        ("/Profile/Me/Edit", "Edit Profile"),                                // ProfileEdit_Title
        ("/Profile/Me/Emails", "Email Addresses"),                           // Emails_Title
        ("/Consent/MyData", "My Data"),                                      // Consent_MyData_Title (served by Consent)
        ("/Profile/Me/Medical", "Medical conditions"),                       // Profile_DietaryMedical_MedicalConditions_Title
        ("/Email/CommunicationPreferences", "Communication Preferences"),    // CommPrefs_Title (served by Email)
        ("/Email/Outbox", "My Emails"),                                      // Email_Outbox_MyTitle (served by Email)
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

        // The data-bearing cards, each against its seeded row: a hollow page passes every other assertion.
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
    /// Both assertions are on content only the row can have produced: a <c>NotContain("&lt;vc:")</c> check passes on
    /// an empty-state page, so the persona's own name and the match badge prove the element bound.
    /// </summary>
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
        // Cookie auth redirects an authenticated-but-unauthorized request to AccessDeniedPath: a 302, not a 403.
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
    /// The resx must ship as <c>Humans.Users</c> satellite assemblies and be found there. A manifest name that doesn't
    /// match the marker type's namespace falls back to English silently.
    /// </summary>
    [HumansFact(Timeout = 180000)]
    public async Task Users_pages_render_in_spanish_from_the_sections_satellite_assemblies()
    {
        // Razor's HtmlEncoder escapes non-ASCII to entities, so every expected string below is ASCII-only.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        // Accept-Language doesn't reach a signed-in user: the PreferredLanguage provider short-circuits the chain. Switch
        // the way the UI does.
        var switcherPage = await (await Client.GetAsync("/Profile/Me", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("culture", "es"),
            ]), ct);

        var profile = await Client.GetAsync("/Profile/Me", ct);
        profile.StatusCode.Should().Be(HttpStatusCode.OK);
        var profileHtml = await profile.Content.ReadAsStringAsync(ct);

        profileHtml.Should().Contain("Mi perfil");                  // Profile_Title
        profileHtml.Should().NotContain("Profile Information",      // Profile_Information
            because: "an English string here means the .es satellite was not found");
        AssertRenderedCleanly(profileHtml, "GET /Profile/Me (es)");

        // One page per prefix family, so a satellite that resolves for Profile_ but not its neighbours still fails.
        foreach (var (url, copy) in new[]
                 {
                     ("/Profile/Me/Edit", "Editar perfil"),          // ProfileEdit_Title
                     ("/Profile/Me/Emails", "Direcciones de correo"),// Emails_Title
                     ("/Consent/MyData", "Mis datos"),               // Consent_MyData_Title (served by Consent)
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

    /// <summary>One row per auto-hiding card. Returns the barrio name the my-camps card renders.</summary>
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

        // CachingTicketQueryService evicts only on its own write pass-throughs. Folding a ticketless user into the
        // persona writes nothing, but the pass-through drops the cached projection and holdings.
        await Factory.Services.GetRequiredService<CachingTicketQueryService>()
            .ReassignAsync(Guid.NewGuid(), userId, userId, now, ct);
    }

    private async Task<string> SeedCampMembershipAsync(Guid userId, CancellationToken ct)
    {
        // The test database has no barrios. Built through ICampSeeding, not CampsDbContext, because only its own write
        // verbs refresh CachingCampService.
        using var scope = Factory.Services.CreateScope();
        var camps = scope.ServiceProvider.GetRequiredService<ICampService>();
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

        // The Camp Lead definition exists only after the system roles are seeded.
        await roleSeeding.SeedSystemRolesAsync(userId, ct);
        var leadDefinitionId = await roleSeeding.GetDefinitionIdBySlugAsync(CampSeedRoles.CampLeadSlug, ct);
        leadDefinitionId.Should().NotBeNull("the Camp Lead role definition must exist after seeding system roles");
        await seeding.AddMemberAndAssignRoleInActiveSeasonAsync(
            camp.Id, leadDefinitionId.Value, userId, userId, ct);

        return CampName;
    }

    private async Task SeedApprovedPersonalEventAsync(Guid userId, CancellationToken ct)
    {
        // The Events decorator has no blanket invalidator; submit and moderate reload its approved-event projection.
        using var scope = Factory.Services.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IEventManagement>();
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
            // The profile card shows personal events only.
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
        // ReSharper can rewrite <vc:name> to <name-view-component>, which also renders inert.
        html.Should().NotContain("-view-component", $"{what} has a rewritten vc tag");

        foreach (var prefix in ProfilePrefixes)
        {
            html.Should().NotContain(prefix, $"{what} rendered a raw {prefix}* resource key");
        }
    }
}
