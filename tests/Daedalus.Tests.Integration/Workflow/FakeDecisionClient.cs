using Daedalus.Agents.Decisions;
using Minos;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     A canned <see cref="IDecisionClient"/> for the end-to-end node selection tests: it answers every request with the
///     same <see cref="NodeQuestions"/> answers and counts the calls, no network. The Integration project keeps its own
///     copy rather than sharing the Unit project's.
/// </summary>
internal sealed class FakeDecisionClient : IDecisionClient
{
    private readonly Result<DecisionResponse, DecisionError> _reply;

    private int _calls;

    private FakeDecisionClient(Result<DecisionResponse, DecisionError> reply) => _reply = reply;

    /// <summary>How many calls arrived. Each decided node visit must ask exactly once.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>
    ///     A client that answers <paramref name="demand"/> with full certainty at <paramref name="confidence"/>, and gives
    ///     each tool family the probability named for it, in <see cref="NodeQuestions"/> order.
    /// </summary>
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

    public ValueTask<Result<DecisionResponse, DecisionError>> EvaluateAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return ValueTask.FromResult(_reply);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
