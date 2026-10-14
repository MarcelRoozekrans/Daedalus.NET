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

    /// <summary>Builds a client whose reply is a failure of this kind.</summary>
    public static FakeDecisionClient Failing(DecisionErrorKind kind) =>
        new(Result<DecisionResponse, DecisionError>.Failure(new DecisionError(kind, "zqx quoted request")));

    /// <summary>How long a call waits, honouring cancellation, before it answers. Default none.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.Zero;

    /// <summary>An exception thrown by every call, before it answers. Default none.</summary>
    public Exception? Throws { get; init; }

    /// <summary>The state of the last request received, or <see langword="null"/> before any call.</summary>
    public string? LastState { get; private set; }

    /// <summary>How many calls arrived.</summary>
    public int Calls { get; private set; }

    public async ValueTask<Result<DecisionResponse, DecisionError>> EvaluateAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        Calls++;
        request.State.TryGetString(out var state);
        LastState = state;
        if (Throws is not null)
        {
            throw Throws;
        }

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        return reply;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
