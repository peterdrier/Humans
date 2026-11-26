using Humans.Agent.Models;
using Humans.Agent.Services.Anthropic;

namespace Humans.Agent.Services;

internal interface IAgentPromptAssembler
{
    string BuildSystemPrompt(string preloadCorpus);
    string BuildUserContextTail(AgentUserContext snapshot);
    IReadOnlyList<AnthropicToolDefinition> BuildToolDefinitions();
}
