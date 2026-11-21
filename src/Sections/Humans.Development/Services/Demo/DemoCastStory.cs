using Humans.Base.Demo;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Development.Services.Demo;

/// <summary>
/// The demo crowd: invented humans every other story draws on. Personas are not part of it; they keep their fixed
/// ids and are never retired.
/// </summary>
internal sealed class DemoCastStory(
    IAccountProvisioningService accountProvisioning,
    IUserEmailService userEmailService,
    IProfileEditorService profileEditor,
    IUserService userService,
    IUserWrite userWrite,
    IAccountDeletionService accountDeletion,
    DevPersonaSeeder personaSeeder,
    IClock clock) : IDemoStory
{
    private const string FakeReason = "Demo cast member";

    private static readonly (string City, string CountryCode, string[] FirstNames)[] Places =
    [
        ("Barcelona", "ES", ["Marta", "Jordi", "Núria", "Pau", "Laia", "Oriol"]),
        ("Madrid", "ES", ["Lucía", "Javier", "Carmen", "Diego", "Elena", "Pablo"]),
        ("Berlin", "DE", ["Lena", "Jonas", "Anna", "Felix", "Mia", "Lukas"]),
        ("Milano", "IT", ["Giulia", "Marco", "Chiara", "Luca", "Sara", "Matteo"]),
        ("Lyon", "FR", ["Camille", "Hugo", "Léa", "Louis", "Chloé", "Jules"]),
        ("Amsterdam", "NL", ["Sanne", "Daan", "Fleur", "Sem", "Noor", "Bram"]),
        ("London", "GB", ["Olivia", "Harry", "Amelia", "Jack", "Isla", "Noah"]),
    ];

    private static readonly string[] LastNames =
    [
        "García", "Puig", "Müller", "Rossi", "Martin", "de Vries", "Smith", "Ferrer", "Schmidt", "Bianchi",
        "Bernard", "Jansen", "Brown", "Romero", "Weber", "Ricci", "Dubois", "Bakker", "Taylor", "Soler",
    ];

    private static readonly string[] BurnerFirst =
    [
        "Dusty", "Glitter", "Solar", "Velvet", "Cosmic", "Neon", "Wander", "Ember", "Lunar", "Pixel",
        "Mirage", "Thunder", "Sparkle", "Feather", "Rusty", "Echo", "Prism", "Nomad", "Saffron", "Breeze",
    ];

    private static readonly string[] BurnerSecond =
    [
        "Fox", "Moth", "Otter", "Comet", "Wren", "Lynx", "Gecko", "Heron", "Panda", "Raven",
    ];

    public DemoStoryKey Key => DemoCast.StoryKey;

    public IReadOnlyList<DemoStoryKey> After => [];

    public async Task TellAsync(IDemoWorld world, CancellationToken ct)
    {
        for (var n = 1; n <= DemoCast.Size; n++)
        {
            var number = n;
            await world.EnsureAsync(
                DemoCast.HumanKey(number),
                async (id, _) => await userService.GetUserInfoAsync(id) is { IsTombstone: false },
                (planned, token) => CreateAsync(planned, number, world, token),
                ct);
        }
    }

    public async Task RetireAsync(IDemoWorld world, CancellationToken ct)
    {
        foreach (var (key, userId) in await world.MadeAsync(DemoCast.HumanKeyPrefix, ct))
        {
            var purge = await accountDeletion.PurgeAsync(userId, world.ActorUserId, ct);
            if (purge.Success)
                await world.MarkRetiredAsync(key, ct);
            else
                await world.MarkLeftBehindAsync(key, $"purge refused: {purge.ErrorKey}", ct);
        }
    }

    /// <summary>
    /// Most of the crowd is an active, consented volunteer marked fake. Every 10th stays unmarked so Consent's review
    /// queue has people in it, every 15th skips the consents, and every 25th stops before the profile, mid-onboarding.
    /// Each step skips what is already done, so a second call finishes a human a cut-off run left half-made.
    /// </summary>
    private async Task<Guid> CreateAsync(Guid userId, int n, IDemoWorld world, CancellationToken ct)
    {
        var place = Places[n % Places.Length];
        var firstName = place.FirstNames[n / Places.Length % place.FirstNames.Length];
        var lastName = LastNames[n * 7 % LastNames.Length];
        var burnerName = $"{BurnerFirst[n % BurnerFirst.Length]} {BurnerSecond[n * 3 % BurnerSecond.Length]} {n:D3}";
        var now = clock.GetCurrentInstant();

        if (await userService.GetUserInfoAsync(userId) is null)
        {
            var errors = await accountProvisioning.CreateAccountAsync(
                userId, burnerName,
                createdAt: now.Minus(Duration.FromDays(n * 3 % 700)),
                lastLoginAt: now.Minus(Duration.FromDays(n % 60)));
            if (errors.Count > 0)
                throw new InvalidOperationException($"Could not create demo human {n}: {string.Join(", ", errors)}");
        }

        // @localhost: not a .local tombstone, and Email never sends to it.
        var email = $"demo-{world.Generation}-{n:D3}@localhost";
        var added = await userEmailService.AddVerifiedEmailAsync(userId, email, ct);
        if (added.ErrorKey is not null)
            throw new InvalidOperationException($"Could not add {email}: {added.ErrorKey}");

        if (n % 25 == 0)
            return userId;

        var request = DevPersonaSeeder.BuildPersonaProfileRequest(
            burnerName, firstName, lastName, bio: "Invented member of the demo world.") with
        {
            City = place.City,
            CountryCode = place.CountryCode,
            BirthdayMonth = n % 12 + 1,
            BirthdayDay = n % 28 + 1,
            Pronouns = null,
        };
        var save = await profileEditor.SaveProfileAsync(userId, request, ct);
        if (!save.Succeeded)
            throw new InvalidOperationException($"Demo human {n}'s profile was refused.");

        if (n % 10 != 0)
            await userWrite.MarkFakeAsync(userId, world.ActorUserId, FakeReason, ct);

        if (DemoCast.IsActiveVolunteer(n))
            await personaSeeder.EnsureActiveAsync(userId);

        return userId;
    }
}
