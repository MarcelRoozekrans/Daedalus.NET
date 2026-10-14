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

    /// <summary>The digest of the question set as <see cref="NodeQuestions.Version"/> 1 asks it.</summary>
    private const string Version1Digest = "F4EB691D83343772F5078F1FAA5C897DD2B3DD50D1494DECC20B38CDF10F5C2F";

    /// <summary>
    ///     Final review item 3: every <c>node-selection</c> record names the question-set version its answers belong to, so
    ///     the version must change whenever the questions do. This pins the version to a digest of every key, kind, question
    ///     text and level description. Red: reword any question; the digest differs and the test asks for a new version.
    ///     Red: raise the version without a change; the version differs.
    /// </summary>
    [Fact]
    public void The_question_set_version_changes_with_the_questions()
    {
        var text = new System.Text.StringBuilder();
        foreach (var question in NodeQuestions.Definition.Questions)
        {
            text.Append(question.Key).Append('|').Append(question.Kind).Append('|').Append(Text(question.Instructions))
                .Append('|').Append(Text(question.WhenTrue)).Append('|').Append(Text(question.WhenFalse));
            foreach (var option in question.Options)
            {
                text.Append('|').Append(option.Key).Append('=').Append(option.Criterion?.Description);
            }

            text.Append('\n');
        }

        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())));

        (NodeQuestions.Version, digest).Should().Be(
            (1, Version1Digest),
            "a changed question set must raise NodeQuestions.Version and pin the new digest here");
    }

    private static string Text(DecisionContent? content) =>
        content is { } value && value.TryGetString(out var text) ? text ?? "" : "";
}
