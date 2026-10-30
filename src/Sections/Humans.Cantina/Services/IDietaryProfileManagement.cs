using Humans.Cantina.Contracts;

namespace Humans.Cantina.Services;

internal interface IDietaryProfileManagement : ICantinaService
{
    /// <summary>Unknown chips are dropped, "Other" text is kept only with "Other" selected, blanks become null. Returns a
    /// resource key when "Other" is selected without text (nothing is written), null on success.</summary>
    Task<string?> SaveAsync(Guid userId, DietaryProfileInfo answers, CancellationToken ct = default);

    /// <summary>Clears every food field and stops the prompts until the member saves answers.</summary>
    Task DeclineAsync(Guid userId, CancellationToken ct = default);
}
