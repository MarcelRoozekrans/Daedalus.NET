using Daedalus.Agents.Decisions;
using Minos;

namespace Daedalus.Tests.Unit.Decisions;

/// <summary>A canned <see cref="IDecisionClient"/>: answers every request with the given response, no network.</summary>
internal sealed class FakeDecisionClient(Result<DecisionResponse, DecisionError> reply) : IDecisionClient
{
    /// <summary>Builds a client whose single reply carries these answers, in <see cref="NodeQuestions"/> order.</summary>
    public static FakeDecisionClient Answering(
        Demand demand, double confidence, double roslyn, double sandbox, double memory, double skills, double context7, double issues)
    {
        var levels = new double[3];
        levels[(int)demand] = 1;
        var answers = new[]
        {
            QuestionAnswer.Score((int)demand, (int)demand, confidence, levels),
            QuestionAnswer.Noul(roslyn),
            QuestionAnswer.Noul(sandbox),
            QuestionAnswer.Noul(memory),
            QuestionAnswer.Noul(skills),
            QuestionAnswer.Noul(context7),
            QuestionAnswer.Noul(issues),
        };
        return new FakeDecisionClient(
            Result<DecisionResponse, DecisionError>.Success(new DecisionResponse(NodeQuestions.Definition, answers, model: "fake")));
    }

    public ValueTask<Result<DecisionResponse, DecisionError>> EvaluateAsync(DecisionRequest request, CancellationToken cancellationToken = default) => new(reply);

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
