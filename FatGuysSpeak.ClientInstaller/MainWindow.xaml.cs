using System.Windows;
using System.Windows.Media;
using FatGuysSpeak.ClientInstaller.Pages;
using Color = System.Windows.Media.Color;

namespace FatGuysSpeak.ClientInstaller;

public partial class MainWindow : Window
{
    public readonly InstallConfig Config;

    private readonly List<IWizardPage> _pages;
    private int _currentIndex;

    private static readonly SolidColorBrush ActiveDot   = new(Color.FromRgb(0xF0, 0x40, 0x10));
    private static readonly SolidColorBrush InactiveDot = new(Color.FromRgb(0x33, 0x33, 0x33));
    private static readonly SolidColorBrush DoneDot     = new(Color.FromRgb(0x36, 0xB8, 0x64));

    public MainWindow(InstallConfig config)
    {
        Config = config;
        InitializeComponent();

        _pages =
        [
            new WelcomePage(Config),
            new OptionsPage(Config),
            new InstallPage(Config),
        ];

        Navigate(0);
    }

    private void Navigate(int index)
    {
        _currentIndex = index;
        var page = _pages[index];
        ContentHost.Content = page;
        page.OnActivated();

        PageTitle.Text = page.Title;

        UpdateDots(index);
        BackButton.IsEnabled  = index > 0 && !page.IsTerminal;
        NextButton.IsEnabled  = page.CanAdvance;
        NextButton.Content    = page.IsTerminal ? "Close" : (index == _pages.Count - 2 ? "Install" : "Next");
        NextButton.Style      = page.IsTerminal
            ? (Style)FindResource("GreenButton")
            : (index == _pages.Count - 2 ? (Style)FindResource("PrimaryButton") : (Style)FindResource("PrimaryButton"));

        page.AdvanceChanged += Page_AdvanceChanged;
    }

    private void Page_AdvanceChanged(object? sender, bool canAdvance)
    {
        NextButton.IsEnabled = canAdvance;
    }

    private void UpdateDots(int active)
    {
        var dots = new[] { Dot0, Dot1, Dot2 };
        for (int i = 0; i < dots.Length; i++)
        {
            if (i < active)       dots[i].Fill = DoneDot;
            else if (i == active) dots[i].Fill = ActiveDot;
            else                  dots[i].Fill = InactiveDot;
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex > 0)
            Navigate(_currentIndex - 1);
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        var page = _pages[_currentIndex];
        if (page.IsTerminal) { Close(); return; }

        if (_currentIndex < _pages.Count - 1)
            Navigate(_currentIndex + 1);
    }
}
