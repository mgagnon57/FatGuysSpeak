using System.Windows.Controls;

namespace FatGuysSpeak.Installer.Pages;

public partial class WelcomePage : UserControl, IWizardPage
{
    public WelcomePage() => InitializeComponent();
    public bool CanAdvance() => true;
    public void OnActivated() { }
}
