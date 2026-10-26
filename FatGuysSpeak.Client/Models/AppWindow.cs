namespace FatGuysSpeak.Client.Models;

public record AppWindow(IntPtr Handle, string Title, string ProcessName, int ProcessId = 0, bool IsLikelyGame = false, string? GameName = null)
{
    public string Icon => IsLikelyGame ? "🎮" : ProcessName.Length > 0 ? "🪟" : "🖥";
    public string DisplayTitle => IsLikelyGame && GameName is not null ? GameName : ProcessName.Length > 0 ? ProcessName : Title;
    public string DisplaySubtitle => IsLikelyGame ? "Likely game" : ProcessName.Length > 0 ? Title : "";
    public override string ToString() => Title;
}
