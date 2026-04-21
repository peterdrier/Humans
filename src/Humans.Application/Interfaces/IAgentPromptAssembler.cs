using Humans.Application.Models;

namespace Humans.Application.Interfaces;

public interface IAgentPromptAssembler
{
    string BuildSystemPrompt(string preloadCorpus);
    string BuildUserContextTail(AgentUserSnapshot snapshot);
    IReadOnlyList<AnthropicToolDefinition> BuildToolDefinitions();
}
