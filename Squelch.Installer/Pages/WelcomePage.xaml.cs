using System.Windows.Controls;

namespace Squelch.Installer.Pages;

public partial class WelcomePage : UserControl, IWizardPage
{
    public WelcomePage() => InitializeComponent();
    public bool CanAdvance() => true;
    public void OnActivated() { }
}
