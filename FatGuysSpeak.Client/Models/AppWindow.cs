namespace FatGuysSpeak.Client.Models;

public record AppWindow(IntPtr Handle, string Title, string ProcessName)
{
    public string Icon => ProcessName.Length > 0 ? "🪟" : "🖥";
    public string DisplayTitle => ProcessName.Length > 0 ? ProcessName : Title;
    public string DisplaySubtitle => ProcessName.Length > 0 ? Title : "";
    public override string ToString() => Title;
}
