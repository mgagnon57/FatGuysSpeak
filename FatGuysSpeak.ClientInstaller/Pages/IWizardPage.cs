namespace FatGuysSpeak.ClientInstaller.Pages;

public interface IWizardPage
{
    string Title { get; }
    bool CanAdvance { get; }
    bool IsTerminal { get; }
    event EventHandler<bool> AdvanceChanged;
    void OnActivated();
}
