using Humans.Agent.Models;
using Humans.Agent.Services.Anthropic;

namespace Humans.Agent.Services;

internal interface IAgentPromptAssembler
{
    string BuildSystemPrompt(string preloadCorpus);
    string BuildUserContextTail(AgentUserSnapshot snapshot);
    IReadOnlyList<AnthropicToolDefinition> BuildToolDefinitions();
}
