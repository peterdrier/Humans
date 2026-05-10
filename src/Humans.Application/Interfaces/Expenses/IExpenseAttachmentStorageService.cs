using Humans.Application.Interfaces;

namespace Humans.Application.Interfaces.Expenses;

public interface IExpenseAttachmentStorageService : IApplicationService
{
    /// <summary>Persists the stream and returns the new attachment id.</summary>
    Task<Guid> StoreAsync(
        Stream content, string extension, string contentType,
        CancellationToken ct = default);

    /// <summary>Opens a stream over the attachment bytes. Caller disposes.</summary>
    Task<Stream> OpenReadAsync(
        Guid id, string extension, CancellationToken ct = default);

    /// <summary>Deletes the on-disk file. Idempotent.</summary>
    Task DeleteAsync(Guid id, string extension, CancellationToken ct = default);
}
