using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Tests.Client;

public class SquelchHandoverPlanTests
{
    private const string LocalAppData = @"C:\Users\me\AppData\Local";

    [Fact]
    public void SquelchExePath_IsDefaultVelopackRoot()
        => Assert.Equal(Path.Combine(LocalAppData, "Squelch.Client", "Squelch.Client.exe"),
                        SquelchHandoverPlan.SquelchExePath(LocalAppData));

    [Theory]
    [InlineData(@"D:\Apps\FatGuysSpeak.Client\current\")]
    [InlineData(@"D:\Apps\FatGuysSpeak.Client\current")]
    [InlineData(@"D:\Apps\FatGuysSpeak.Client\Current\")]
    public void UpdateExePath_FromCurrentFolder_UsesItsRoot(string baseDir)
        => Assert.Equal(Path.Combine(@"D:\Apps\FatGuysSpeak.Client", "Update.exe"),
                        SquelchHandoverPlan.UpdateExePath(baseDir, LocalAppData));

    [Fact]
    public void UpdateExePath_NotInCurrentFolder_FallsBackToDefaultRoot()
        => Assert.Equal(Path.Combine(LocalAppData, "FatGuysSpeak.Client", "Update.exe"),
                        SquelchHandoverPlan.UpdateExePath(@"C:\src\bin\Debug\", LocalAppData));

    [Fact]
    public void SetupFailure_SuccessWithExe_IsNull()
        => Assert.Null(SquelchHandoverPlan.SetupFailure(0, true));

    [Fact]
    public void SetupFailure_NonZeroExit_ReportsCode()
        => Assert.Contains("exit code 5", SquelchHandoverPlan.SetupFailure(5, true));

    [Fact]
    public void SetupFailure_ZeroExitButNoExe_IsFailure()
        => Assert.NotNull(SquelchHandoverPlan.SetupFailure(0, false));
}
