using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Tests.Server;

public class VersionSyncPlanTests
{
    // ── Same major: always connect as-is, regardless of minor/patch direction ──
    [Theory]
    [InlineData("1.0.0", "1.0.0")]   // identical
    [InlineData("1.0.0", "1.9.9")]   // server ahead within major
    [InlineData("1.9.9", "1.0.0")]   // client ahead within major (no needless downgrade)
    [InlineData("2.3.4", "2.0.0")]
    [InlineData("v1.0.0", "1.5.0")]  // v-prefix tolerated
    [InlineData("10.0.0", "10.4.1")] // multi-digit major
    public void Decide_SameMajor_ConnectAsIs(string mine, string server)
        => Assert.Equal(VersionSyncAction.ConnectAsIs, VersionSyncPlan.Decide(mine, server));

    // ── Cross-major, server newer: upgrade ──
    [Theory]
    [InlineData("1.0.0", "2.0.0")]
    [InlineData("1.9.9", "2.0.0")]
    [InlineData("1.0.0", "3.0.0")]
    [InlineData("2.5.0", "10.0.0")]
    [InlineData("v1.0.0", "2.0.0")]
    public void Decide_CrossMajor_ServerNewer_Upgrade(string mine, string server)
        => Assert.Equal(VersionSyncAction.Upgrade, VersionSyncPlan.Decide(mine, server));

    // ── Cross-major, server older: downgrade ──
    [Theory]
    [InlineData("2.0.0", "1.0.0")]
    [InlineData("2.0.0", "1.9.9")]
    [InlineData("3.0.0", "1.0.0")]
    [InlineData("10.0.0", "2.5.0")]
    [InlineData("v3.1.4", "1.0.0")]
    public void Decide_CrossMajor_ServerOlder_Downgrade(string mine, string server)
        => Assert.Equal(VersionSyncAction.Downgrade, VersionSyncPlan.Decide(mine, server));

    // ── Cannot evaluate: missing/unparseable inputs => never disrupt the user ──
    [Theory]
    [InlineData(null, "2.0.0")]      // dev / not Velopack-installed
    [InlineData("", "2.0.0")]
    [InlineData("1.0.0", null)]      // server version endpoint failed
    [InlineData("1.0.0", "")]
    [InlineData("garbage", "2.0.0")]
    [InlineData("1.0.0", "not-a-version")]
    [InlineData("1.2", "2.0.0")]     // short / malformed
    [InlineData(null, null)]
    public void Decide_UnparseableOrMissing_CannotEvaluate(string? mine, string? server)
        => Assert.Equal(VersionSyncAction.CannotEvaluate, VersionSyncPlan.Decide(mine, server));

    // ── The decision is symmetric in the obvious way: swapping a cross-major pair flips up/down ──
    [Theory]
    [InlineData("1.0.0", "2.0.0")]
    [InlineData("1.4.0", "5.2.0")]
    public void Decide_CrossMajor_IsDirectionallySymmetric(string lower, string higher)
    {
        Assert.Equal(VersionSyncAction.Upgrade, VersionSyncPlan.Decide(lower, higher));
        Assert.Equal(VersionSyncAction.Downgrade, VersionSyncPlan.Decide(higher, lower));
    }
}
