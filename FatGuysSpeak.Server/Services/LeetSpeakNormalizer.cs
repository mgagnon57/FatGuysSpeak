using System.Text.RegularExpressions;

namespace FatGuysSpeak.Server.Services;

public static class LeetSpeakNormalizer
{
    private static readonly (Regex Pattern, string Replacement)[] Substitutions =
    [
        (new Regex(@"4|@", RegexOptions.Compiled), "a"),
        (new Regex(@"3", RegexOptions.Compiled), "e"),
        (new Regex(@"1|!", RegexOptions.Compiled), "i"),
        (new Regex(@"0", RegexOptions.Compiled), "o"),
        (new Regex(@"5|\$", RegexOptions.Compiled), "s"),
        (new Regex(@"7", RegexOptions.Compiled), "t"),
        (new Regex(@"\+", RegexOptions.Compiled), "t"),
        (new Regex(@"8", RegexOptions.Compiled), "b"),
        (new Regex(@"6", RegexOptions.Compiled), "g"),
        (new Regex(@"\(", RegexOptions.Compiled), "c"),
        (new Regex(@"[^a-z0-9\s]", RegexOptions.Compiled | RegexOptions.IgnoreCase), ""),
    ];

    public static string Normalize(string input)
    {
        var result = input.ToLowerInvariant();
        foreach (var (pattern, replacement) in Substitutions)
            result = pattern.Replace(result, replacement);
        return result;
    }
}
