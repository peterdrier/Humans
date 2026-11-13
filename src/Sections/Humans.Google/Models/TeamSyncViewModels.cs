using Humans.Google.Contracts;

namespace Humans.Google.Models;

internal sealed class SyncTabContentViewModel
{
    public required SyncPreviewResult Result { get; init; }
    public required string ResourceType { get; init; }
}
