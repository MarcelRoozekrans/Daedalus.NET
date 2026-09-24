namespace Daedalus.Agents.Workflow;

/// <summary>
///     A minimal unified-style line diff: an LCS (longest common subsequence) over the two texts' lines, walked
///     forward from the start, emitting a context line prefixed with a space for a line common to both texts,
///     <c>-</c> for a line found only in the old text and <c>+</c> for a line found only in the new one. Built
///     for <see cref="StandingInstructionsWriter.Diff"/>, where both texts are at most a few KB of a standing
///     instructions file — an O(lines²) table is not a concern at that size.
/// </summary>
internal static class LineDiff
{
    /// <summary>
    ///     Computes the line diff of <paramref name="oldText"/> against <paramref name="newText"/>, newline-joined.
    ///     An empty string is treated as zero lines, not one empty line, so diffing against an absent or empty
    ///     file does not spuriously report a single blank context/removed line.
    /// </summary>
    public static string Compute(string oldText, string newText)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);
        var lengths = LcsLengths(oldLines, newLines);
        return string.Join('\n', Walk(oldLines, newLines, lengths));
    }

    private static string[] SplitLines(string text) =>
        string.IsNullOrEmpty(text) ? [] : text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    /// <summary>
    ///     <c>lengths[i][j]</c> is the LCS length of <c>oldLines[i..]</c> and <c>newLines[j..]</c>, filled from
    ///     the bottom-right corner so <see cref="Walk"/> can read it forward without recursion. A jagged array,
    ///     not <c>int[,]</c>: CA1814 flags a rectangular array here as the wrong shape for this access pattern.
    /// </summary>
    private static int[][] LcsLengths(string[] oldLines, string[] newLines)
    {
        var lengths = new int[oldLines.Length + 1][];
        for (var i = 0; i <= oldLines.Length; i++)
        {
            lengths[i] = new int[newLines.Length + 1];
        }

        for (var i = oldLines.Length - 1; i >= 0; i--)
        {
            for (var j = newLines.Length - 1; j >= 0; j--)
            {
                lengths[i][j] = string.Equals(oldLines[i], newLines[j], StringComparison.Ordinal)
                    ? lengths[i + 1][j + 1] + 1
                    : Math.Max(lengths[i + 1][j], lengths[i][j + 1]);
            }
        }

        return lengths;
    }

    private static List<string> Walk(string[] oldLines, string[] newLines, int[][] lengths)
    {
        var output = new List<string>();
        var i = 0;
        var j = 0;
        while (i < oldLines.Length && j < newLines.Length)
        {
            if (string.Equals(oldLines[i], newLines[j], StringComparison.Ordinal))
            {
                output.Add(" " + oldLines[i]);
                i++;
                j++;
            }
            else if (lengths[i + 1][j] >= lengths[i][j + 1])
            {
                output.Add("-" + oldLines[i]);
                i++;
            }
            else
            {
                output.Add("+" + newLines[j]);
                j++;
            }
        }

        while (i < oldLines.Length)
        {
            output.Add("-" + oldLines[i]);
            i++;
        }

        while (j < newLines.Length)
        {
            output.Add("+" + newLines[j]);
            j++;
        }

        return output;
    }
}
