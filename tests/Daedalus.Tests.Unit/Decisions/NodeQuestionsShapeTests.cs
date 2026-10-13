using Daedalus.Agents.Decisions;
using Minos;

namespace Daedalus.Tests.Unit.Decisions;

public class NodeQuestionsShapeTests
{
    /// <summary>
    ///     The drift guard: a changed question set fails here until the thresholds are revisited.
    ///     Red: rename a property of NodeQuestions or an enum member of Demand and the matching assert fails.
    /// </summary>
    [Fact]
    public void NodeQuestions_shape_is_pinned()
    {
        var questions = NodeQuestions.Definition.Questions.ToArray();

        questions.Select(q => q.Key).Should().Equal(
            "demand", "needs_roslyn", "needs_sandbox", "needs_memory", "needs_skills", "needs_context7", "needs_issues");
        questions.Select(q => q.Kind).Should().Equal(
            QuestionKind.Score, QuestionKind.Noul, QuestionKind.Noul, QuestionKind.Noul, QuestionKind.Noul, QuestionKind.Noul,
            QuestionKind.Noul);
        Enum.GetNames<Demand>().Should().Equal("Routine", "Moderate", "Hard");
        questions[0].Options.Select(o => o.Criterion?.Description?.Split(' ')[0]).Should().Equal("Routine", "Moderate", "Hard");
    }
}
