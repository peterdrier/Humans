using Humans.Domain.Enums;

namespace Humans.Application.Interfaces;

public interface IAgentPreloadCorpusBuilder
{
    Task<string> BuildAsync(AgentPreloadConfig config, CancellationToken cancellationToken = default);
}
