using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace FatGuysSpeak.ClientInstaller.Pages;

public partial class WelcomePage : UserControl, IWizardPage
{
    public string Title => "Welcome";
    public bool CanAdvance => true;
    public bool IsTerminal => false;
#pragma warning disable CS0067
    public event EventHandler<bool>? AdvanceChanged;
#pragma warning restore CS0067

    public WelcomePage(InstallConfig config)
    {
        InitializeComponent();
    }

    public void OnActivated() { }
}
