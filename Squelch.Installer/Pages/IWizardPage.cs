namespace Squelch.Installer.Pages;

public interface IWizardPage
{
    bool CanAdvance();
    void OnActivated();
}
