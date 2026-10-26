using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Tests.Client;

public class SquelchHandoverPlanTests
{
    private const string LocalAppData = @"C:\Users\me\AppData\Local";

    [Fact]
    public void SquelchExePath_IsDefaultVelopackRoot()
        => Assert.Equal(Path.Combine(LocalAppData, "Squelch.Client", "Squelch.Client.exe"),
                        SquelchHandoverPlan.SquelchExePath(LocalAppData));

    [Fact]
    public void SquelchCurrentExePath_IsInsideCurrent()
        => Assert.Equal(Path.Combine(LocalAppData, "Squelch.Client", "current", "Squelch.Client.exe"),
                        SquelchHandoverPlan.SquelchCurrentExePath(LocalAppData));

    [Fact]
    public void SquelchVersionFilePath_IsCurrentSqVersion()
        => Assert.Equal(Path.Combine(LocalAppData, "Squelch.Client", "current", "sq.version"),
                        SquelchHandoverPlan.SquelchVersionFilePath(LocalAppData));

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]   // half-extracted current\ (no sq.version yet)
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]  // e.g. only the root stub exists
    public void IsCompletedInstall_NeedsCurrentExeAndSqVersion(bool exe, bool version, bool expected)
        => Assert.Equal(expected, SquelchHandoverPlan.IsCompletedInstall(exe, version));

    [Fact]
    public void Decide_SquelchAlreadyInstalled_SkipsInstall()
        => Assert.Equal(HandoverStart.OpenExisting, SquelchHandoverPlan.Decide(squelchInstalled: true));

    [Fact]
    public void Decide_NoSquelch_DownloadsAndInstalls()
        => Assert.Equal(HandoverStart.DownloadAndInstall, SquelchHandoverPlan.Decide(squelchInstalled: false));

    [Fact]
    public void Decide_PartialInstall_Reinstalls()
        => Assert.Equal(HandoverStart.DownloadAndInstall,
            SquelchHandoverPlan.Decide(SquelchHandoverPlan.IsCompletedInstall(currentExeExists: true, versionFileExists: false)));

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
