using Humans.Surveys.Contracts;
using System.Runtime.CompilerServices;

// Castle DynamicProxy, behind NSubstitute in Humans.Surveys.Tests, needs to see the
// internal ISurveyRepository / ISurveyService to proxy them.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
