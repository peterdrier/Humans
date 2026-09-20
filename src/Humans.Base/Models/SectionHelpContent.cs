namespace Humans.Base.Models;

/// <summary>
/// The one help block Base still owns: the cross-section FAQ. Per-section Guide and
/// Glossary markdown is contributed by the owning section through
/// <see cref="Humans.Base.Interfaces.ISectionHelp"/> and lives in that section's
/// <c>Docs/help/</c> folder. Content is updated via code releases — no admin editing.
/// </summary>
public static class SectionHelpContent
{
    /// <summary>
    /// Frequently-asked questions distilled from real production agent conversations
    /// (audit 2026-05-23). Preloaded every turn via <see cref="Humans.Base.Interfaces.IAgentPreloadAugmentor"/>
    /// so the agent answers these directly instead of routing to support. Routes, button
    /// labels, and procedures were verified against the live controllers and views; the
    /// ticket-policy answers were confirmed with the organisers.
    /// </summary>
    public const string Faq = """
        ## Tickets

        **Q: Can I transfer my ticket to my partner / someone else?**
        Yes. The preferred way is in-app: go to /Tickets/Transfers and request a transfer to the
        receiver (they need their own Humans account). The tickets team approves it and the ticket is
        reissued in their name. You can also email tickets@nobodies.team, but the in-app request is
        better — same team either way, and the app makes it clear who is sending and receiving. One
        request per ticket.

        **Q: I bought a ticket under a different email address.**
        Add that email at /Profile/Me/Emails. The order relinks to your account automatically on the
        next sync (within ~15 minutes). If it was your login email and you cannot re-add it, that needs
        an admin.

        **Q: I'm working shifts — when do I get my early-entry ticket?**
        Early entry is automatic for people working shifts — generally from the day before your first
        shift. For specifics, contact the volunteer coordinators.

        **Q: How many shifts do I need to work to get a ticket?**
        Working shifts does not earn you an entry ticket (for 2026). What it gets you is early-entry
        access and cantina food. You still need your own event ticket.

        **Q: How do I get a low-income / discount ticket?**
        The low-income ticket process ran in March 2026 and has since closed.

        **Q: I have an acompañante / reduced-mobility companion ticket.**
        Email tickets@nobodies.team — they handle companion and accessibility ticket requests.

        ## Shifts

        **Q: Where do I see my shifts?**
        /Shifts/Mine — your personal shift list. The main /Shifts page is for browsing and signing up.

        **Q: How do I sign up for a shift?**
        Browse open shifts at /Shifts, hit Sign Up on the slot (or "Sign up for N dates" for a
        multi-day block). You will get a confirmation showing the phase, dates, and when you are
        expected on site. Most shifts confirm immediately; some need coordinator approval. A coordinator
        can also "voluntell" (directly assign) you.

        **Q: How do I cancel / decline / withdraw from a shift? (I signed up by mistake)**
        Go to /Shifts/Mine. Each shift has a Bail / Withdraw button — it asks for confirmation, then
        removes you. For a multi-day block, Bail drops all days at once.

        **Q: My shifts aren't showing up.**
        Check /Shifts/Mine directly. If a confirmed shift is genuinely missing there, report it via
        /Issues.

        **Q: Which shifts need help / are understaffed?**
        Browse /Shifts and look for slots with open spots — you can filter by department, dates, and
        phase.

        ## Profile

        **Q: What's missing to complete my profile?**
        Two separate things. To be an active member, all you need is your name and to have signed the
        required consent documents — check /Legal; access does not depend on a "100% profile". The
        completion bar on your Home dashboard tracks profile enrichment: profile picture (the biggest
        single chunk), bio, pronouns, city/country, date of birth, what you'd like to contribute,
        emergency contact, languages, your burner history (or ticking "no prior burn experience"), and
        shift-type preferences. Fill any of these at /Profile/Me.

        **Q: How do I change my email / add a new one?**
        Go to /Profile/Me/Emails, add the new address (you will get a verification link sent to it),
        then set it as your primary. Your sign-in is tied to your Google login / magic-link, which is
        separate from the addresses listed here.

        **Q: How do I read messages sent to me / where is my inbox?**
        There is no in-app inbox. When someone uses the "Send <name> a message" button on your
        profile, the message is relayed to your registered email address — check the inbox for the
        address listed at /Profile/Me/Emails. Whether you can reply is the sender's choice: they
        tick "include my contact info" when writing, and only then does the email carry their
        address as the reply-to. Without it the message arrives with no way to write back.

        **Q: Why does the "Send <name> a message" button appear on some profiles but not others?**
        It shows only when all three are true: it is not your own profile; the person has no email
        address set to "all active profiles" visibility (if their address is already visible to you,
        you can just email them directly); and they have not switched off facilitated messages in
        their communication preferences. Messages are capped at 2000 characters.

        **Q: How do I find a specific person?**
        Use the magnifying glass in the top nav, or go to /Search, and type at least two characters.
        For people it matches the display name plus whatever they have chosen to make public — city,
        bio, what they'd like to contribute, pronouns, and contact details set to "all active
        profiles" visibility — so a city name does work as a search term. It does not search
        languages, and it never matches anything someone has kept private. A common first name is
        still hard to pin down: if the person has shared little, you may get many matches or none.
        Search does not look people up by id — if you have their profile id, go straight to
        /Profile/<id> instead. /Team lists people you share a team with, and the community channels
        linked in the footer at https://nobodies.team/ are the way to reach someone otherwise.

        ## Account & privacy

        **Q: How do I delete my account / download my data?**
        Go to /Profile/Me/Privacy — export your data or delete your account there.

        ## About the assistant / out of scope

        **Q: Are you an AI? What can you do?**
        Yes — I'm an AI helper built into the app. I answer questions about how the Humans system works
        (shifts, teams, camps, profiles, governance, tickets) and can look up your own teams, roles, and
        history. I can't act for you (sign you up, edit your profile) or see other people's data.

        **Q: Car sharing / forum / external community / other comms channels?**
        That's outside the Humans app. The main site is https://nobodies.team/ — links for the community
        channels (Discord, Telegram, WhatsApp) are in the footer there.
        """;
}
