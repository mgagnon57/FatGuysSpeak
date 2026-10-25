using System.Text.RegularExpressions;

namespace FatGuysSpeak.Tests.Client;

// The client is MAUI, so it can't be referenced here. Instead, scan its source: every
// "{Binding FooCommand}" in XAML must have a [RelayCommand] Foo/FooAsync method or an explicit
// FooCommand property. A missing command makes the binding fail silently (the button does nothing),
// e.g. the settings gear broke when a new method was inserted under OpenSettings' [RelayCommand].
public class XamlCommandBindingTests
{
    private static string ClientDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "FatGuysSpeak.Client")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "FatGuysSpeak.Client");
    }

    private static IEnumerable<string> SourceFiles(string pattern) =>
        Directory.EnumerateFiles(ClientDir(), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    private static HashSet<string> DefinedCommands()
    {
        var defined = new HashSet<string>();
        // [RelayCommand] (optionally with args / other attributes) directly above the method declaration
        var relay = new Regex(@"\[RelayCommand[^\]]*\]\s*(?:\[[^\]]*\]\s*)*(?:public|private|internal|protected)[^\(\n]*?\s(\w+)\s*\(");
        var explicitProp = new Regex(@"\b(\w+Command)\s*(?:\{|=>)");
        foreach (var file in SourceFiles("*.cs"))
        {
            var src = File.ReadAllText(file);
            foreach (Match m in relay.Matches(src))
            {
                var name = m.Groups[1].Value;
                if (name.EndsWith("Async")) name = name[..^5];
                defined.Add(name + "Command");
            }
            foreach (Match m in explicitProp.Matches(src))
                defined.Add(m.Groups[1].Value);
        }
        return defined;
    }

    [Fact]
    public void EveryXamlCommandBinding_HasAGeneratedOrExplicitCommand()
    {
        var defined = DefinedCommands();
        var binding = new Regex(@"\{Binding\s+(?:Path=)?(?:[\w\.]+\.)?(\w+Command)\b");

        var missing = SourceFiles("*.xaml")
            .SelectMany(f => binding.Matches(File.ReadAllText(f))
                .Select(m => $"{Path.GetFileName(f)}: {m.Groups[1].Value}"))
            .Where(s => !defined.Contains(s[(s.IndexOf(": ") + 2)..]))
            .Distinct()
            .ToList();

        Assert.Empty(missing);
    }
}
