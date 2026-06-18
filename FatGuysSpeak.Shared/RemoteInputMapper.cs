namespace FatGuysSpeak.Shared;

/// <summary>
/// Maps a controller's normalized (0..1) click position within the streamed frame
/// to an absolute screen pixel inside the sharer's capture rectangle. Pure logic so
/// it is unit-testable without Win32 (the SendInput conversion lives in the client).
/// </summary>
public static class RemoteInputMapper
{
    public static (int X, int Y) ToScreenPixel(
        double normX, double normY, int rectLeft, int rectTop, int rectWidth, int rectHeight)
    {
        var nx = Math.Clamp(normX, 0.0, 1.0);
        var ny = Math.Clamp(normY, 0.0, 1.0);
        var x = rectLeft + (int)Math.Round(nx * rectWidth);
        var y = rectTop + (int)Math.Round(ny * rectHeight);
        return (x, y);
    }
}
