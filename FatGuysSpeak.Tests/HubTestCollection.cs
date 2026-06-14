// Hub tests share static ConcurrentDictionary state inside ChatHub.
// Putting them in a [Collection] forces xUnit to run them serially,
// preventing one test's Dispose from clearing another test's state mid-run.
namespace FatGuysSpeak.Tests;

[CollectionDefinition("HubTests", DisableParallelization = true)]
public class HubTestCollection;
