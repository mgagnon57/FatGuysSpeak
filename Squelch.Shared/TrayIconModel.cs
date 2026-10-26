namespace Squelch.Shared;

public enum TrayVisual { Idle, Muted, Level }

/// <summary>Level is 1..7 only when Visual is Level, otherwise 0.</summary>
public readonly record struct TrayIconChoice(TrayVisual Visual, int Level);

/// <summary>Maps voice state + mic level to a tray icon. Update runs on every mic-level event, so it returns
/// null unless the icon actually changes. Swaps between Idle and Level frames (including a level hovering at the
/// gate) are capped at ~12 fps since icon swaps are costly; anything involving Muted, or leaving voice, is immediate.</summary>
public sealed class TrayIconModel
{
    public const int MinFrameIntervalMs = 80;
    private const int MaxLevel = 7;

    private readonly double _smoothing;
    private double _smoothed;
    private TrayIconChoice? _last;
    private long _lastReturnMs;

    public TrayIconModel(double smoothing = 0.5) => _smoothing = smoothing;

    /// <summary>Returns the new icon when it changes, null when unchanged or throttled.</summary>
    public TrayIconChoice? Update(bool inVoice, bool muted, bool transmitting,
                                  double micLevel, double gateThreshold, long nowMs)
    {
        _smoothed = Math.Clamp(_smoothed + _smoothing * (micLevel - _smoothed), 0, 1);

        TrayIconChoice choice;
        if (!inVoice) choice = new(TrayVisual.Idle, 0);
        else if (muted) choice = new(TrayVisual.Muted, 0);
        else if (!transmitting || _smoothed < gateThreshold) choice = new(TrayVisual.Idle, 0);
        else choice = new(TrayVisual.Level, Math.Max(1, (int)Math.Round(_smoothed * MaxLevel)));

        if (choice == _last) return null;
        // Idle/Level frame change: hold back until the frame interval has passed (a later call will return it).
        // Leaving voice and any Muted transition bypass the throttle.
        if (inVoice && _last is { } prev && prev.Visual != TrayVisual.Muted && choice.Visual != TrayVisual.Muted
            && nowMs - _lastReturnMs < MinFrameIntervalMs) return null;

        _last = choice;
        _lastReturnMs = nowMs;
        return choice;
    }
}
