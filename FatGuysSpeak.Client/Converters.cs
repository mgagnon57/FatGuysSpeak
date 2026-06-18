using System.Globalization;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Client.Services;

namespace FatGuysSpeak.Client;

public class StringToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        !string.IsNullOrEmpty(value as string);
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class InvertBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;
}

public class InitialsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && s.Length > 0)
            return s.Length >= 2 ? $"{s[0]}{s[1]}".ToUpper() : s[0].ToString().ToUpper();
        return "?";
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class ChannelIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ChannelType t ? (t == ChannelType.Voice ? "🔊" : "#") : "#";
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class MuteButtonConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "🔇" : "🎤";
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class DeafenButtonConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "🔕" : "🔔";
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class MutedBgConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? ThemeService.Get("ThemeAccentMuted") : ThemeService.Get("ThemeBgElevated");
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class SelectedFontConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? FontAttributes.Bold : FontAttributes.None;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class VoiceStatusLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "In Voice" : "Not in Voice";
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class ChannelSelectionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? ThemeService.Get("ThemeBgActive") : Colors.Transparent;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class ChannelTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? ThemeService.Get("ThemeTextPrimary") : ThemeService.Get("ThemeTextSecondary");
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class NullToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not null;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class VoiceJoinButtonConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "📵 Leave Voice" : "🔊 Join Voice";
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class VoiceJoinColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Color.FromArgb(value is true ? "#ed4245" : "#23a55a");
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class TestMicButtonConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "🎵 On" : "🎵 Off";
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class TestMicColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Color.FromArgb(value is true ? "#23a55a" : "#4e5058");
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class LoopbackColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Color.FromArgb(value is true ? "#f0a030" : "#4e5058");
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class MicLevelColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = value is double d ? d : 0.0;
        if (level > 0.75) return Color.FromArgb("#ed4245");
        if (level > 0.4) return Color.FromArgb("#f0a030");
        return Color.FromArgb("#23a55a");
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class DictateColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Color.FromArgb(value is true ? "#ed4245" : "#3a3a3a");
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class UtcToLocalTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTime dt ? dt.ToLocalTime().ToString("h:mm:ss tt") : string.Empty;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

// true (selected tab) → blue; false → dark
public class TabBgConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? ThemeService.Get("ThemeAccent") : ThemeService.Get("ThemeBgPanel");
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

// @mention messages get a subtle amber tint
public class MentionBgConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? ThemeService.Get("ThemeAccentMuted") : Colors.Transparent;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

// Unread badge: red pill if there are unread messages
public class UnreadBadgeBgConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? ThemeService.Get("ThemeAccent") : Colors.Transparent;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

// Status dot: green=online, amber=away, red=DND, grey=offline
public class StatusDotColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is UserStatus s ? s switch
        {
            UserStatus.Online => Color.FromArgb("#23a55a"),
            UserStatus.Away => Color.FromArgb("#f0a030"),
            UserStatus.DoNotDisturb => Color.FromArgb("#ed4245"),
            _ => Color.FromArgb("#555555"),
        } : Color.FromArgb("#555555");
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

// Reaction pill: highlighted blue if the current user reacted, dark otherwise
public class ReactionBgConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? ThemeService.Get("ThemeAccentMuted") : ThemeService.Get("ThemeBgElevated");
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

// Active toggle button text: blue when active, grey when inactive
public class ActiveButtonTextColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? ThemeService.Get("ThemeAccentLight") : ThemeService.Get("ThemeTextSecondary");
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

// Reaction pill border: bright blue if own, subtle grey otherwise
public class ReactionStrokeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? ThemeService.Get("ThemeAccent") : ThemeService.Get("ThemeDivider");
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
