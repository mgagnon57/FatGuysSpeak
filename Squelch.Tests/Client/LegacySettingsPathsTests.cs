using Squelch.Shared;
using Xunit;

namespace Squelch.Tests.Client;

public class LegacySettingsPathsTests
{
    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\User Name\com.squelch.client\Data")]
    [InlineData(@"C:\Users\x\AppData\Local\User Name\com.squelch.client\Data\")]
    public void Derives_new_and_sibling_old_settings_dirs(string appData)
    {
        var (n, o) = LegacySettingsPaths.FromAppDataDirectory(appData);
        Assert.Equal(Path.Combine(@"C:\Users\x\AppData\Local\User Name\com.squelch.client", "Settings"), n);
        Assert.Equal(Path.Combine(@"C:\Users\x\AppData\Local\User Name", LegacySettingsImport.OldAppId, "Settings"), o);
    }
}
