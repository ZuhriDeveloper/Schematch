using System.Text;

namespace Schematch.Core.Compare;

public static class TextNormalizer
{
    /// <summary>
    /// Normalizes a module definition (view/proc/function/trigger body) for comparison:
    /// unifies line endings, trims trailing whitespace, drops leading/trailing blank lines,
    /// and optionally collapses every whitespace run to a single space.
    /// </summary>
    public static string NormalizeModule(string text, bool collapseWhitespace)
    {
        if (string.IsNullOrEmpty(text)) return "";

        if (collapseWhitespace)
            return CollapseWhitespace(text);

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder(text.Length);
        int lastNonBlank = Array.FindLastIndex(lines, l => l.Trim().Length > 0);
        int firstNonBlank = Array.FindIndex(lines, l => l.Trim().Length > 0);
        for (int i = Math.Max(firstNonBlank, 0); i <= lastNonBlank; i++)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(lines[i].TrimEnd());
        }
        return sb.ToString();
    }

    /// <summary>
    /// Normalizes a scalar SQL expression (default value, check clause, computed column)
    /// for comparison: lowercase, whitespace collapsed, redundant outer parentheses removed.
    /// </summary>
    public static string NormalizeExpression(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return "";
        string s = CollapseWhitespace(expression).ToLowerInvariant();
        return StripOuterParens(s);
    }

    private static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool inWs = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                inWs = true;
                continue;
            }
            if (inWs && sb.Length > 0) sb.Append(' ');
            inWs = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string StripOuterParens(string s)
    {
        while (s.Length >= 2 && s[0] == '(' && s[^1] == ')')
        {
            // Only strip when the parens actually wrap the whole expression: "(a)+(b)" must survive.
            int depth = 0;
            bool wraps = true;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '(') depth++;
                else if (s[i] == ')')
                {
                    depth--;
                    if (depth == 0 && i < s.Length - 1) { wraps = false; break; }
                }
            }
            if (!wraps) break;
            s = s[1..^1].Trim();
        }
        return s;
    }
}
