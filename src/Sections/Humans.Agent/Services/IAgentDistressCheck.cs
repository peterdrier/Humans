namespace Humans.Agent.Services;

internal interface IAgentDistressCheck
{
    bool Matches(string question);
}
