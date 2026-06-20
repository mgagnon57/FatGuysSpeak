// Hub tests share static ConcurrentDictionary state inside ChatHub.
// Putting them in a [Collection] forces xUnit to run them serially,
// preventing one test's Dispose from clearing another test's state mid-run.
namespace FatGuysSpeak.Tests;

[CollectionDefinition("HubTests", DisableParallelization = true)]
public class HubTestCollection;

// Tests that mutate the static BotService.BotUserId must not run in parallel with each
// other, or one class's value bleeds into another's assertions.
[CollectionDefinition("BotState", DisableParallelization = true)]
public class BotStateTestCollection;
