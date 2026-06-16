using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FatGuysSpeak.Installer.Pages;

namespace FatGuysSpeak.Installer;

public partial class MainWindow : Window
{
    public readonly InstallConfig Config = new();

    private readonly UserControl[] _pages;
    private int _step;

    private readonly SolidColorBrush _activeBrush  = new(Color.FromRgb(0xf0, 0x40, 0x10));
    private readonly SolidColorBrush _doneBrush    = new(Color.FromRgb(0x88, 0x28, 0x08));
    private readonly SolidColorBrush _pendingBrush = new(Color.FromRgb(0x25, 0x25, 0x25));

    private readonly (SolidColorBrush fill, TextBlock label)[] _dots;

    public MainWindow()
    {
        InitializeComponent();

        var welcomePage     = new WelcomePage();
        var credentialsPage = new CredentialsPage(Config);
        var networkPage     = new NetworkPage(Config);
        var optionsPage     = new OptionsPage(Config);
        var installPage     = new InstallPage(Config);

        installPage.InstallComplete += OnInstallComplete;

        _pages = [welcomePage, credentialsPage, networkPage, optionsPage, installPage];

        _dots =
        [
            (Dot0Fill, Lbl0),
            (Dot1Fill, Lbl1),
            (Dot2Fill, Lbl2),
            (Dot3Fill, Lbl3),
            (Dot4Fill, Lbl4),
        ];

        GoToStep(0);
    }

    private void GoToStep(int step)
    {
        _step = step;
        ContentHost.Content = _pages[step];

        if (_pages[step] is IWizardPage wp)
            wp.OnActivated();

        UpdateStepDots();
        UpdateButtons();
    }

    private void UpdateStepDots()
    {
        for (var i = 0; i < _dots.Length; i++)
        {
            var (fill, lbl) = _dots[i];
            if (i < _step)
            {
                fill.Color = _doneBrush.Color;
                lbl.Foreground = _doneBrush;
            }
            else if (i == _step)
            {
                fill.Color = _activeBrush.Color;
                lbl.Foreground = _activeBrush;
            }
            else
            {
                fill.Color = _pendingBrush.Color;
                lbl.Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            }
        }
    }

    private void UpdateButtons()
    {
        BtnBack.Visibility = _step == 0 || _step == 4 ? Visibility.Collapsed : Visibility.Visible;
        BtnCancel.Visibility = _step == 4 ? Visibility.Collapsed : Visibility.Visible;
        BtnNext.Visibility = _step == 4 ? Visibility.Collapsed : Visibility.Visible;
        BtnNext.Content = _step == 3 ? "Install" : "Next →";
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step > 0)
            GoToStep(_step - 1);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_pages[_step] is IWizardPage wp && !wp.CanAdvance())
            return;

        if (_step < _pages.Length - 1)
            GoToStep(_step + 1);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Cancel the installation?", "FatGuysSpeak Setup",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            Close();
    }

    private void OnInstallComplete()
    {
        UpdateStepDots();
    }
}
