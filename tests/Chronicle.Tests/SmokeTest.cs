using Chronicle.Models.Recurrence;

namespace Chronicle.Tests;

/// <summary>
/// Proves the test project is wired up: xUnit discovers tests, the
/// Chronicle reference resolves, and a real production type (here
/// <see cref="RecurrenceRule"/>) is reachable from test code.
///
/// Replace or delete once Layer 1 tests land.
/// </summary>
public class SmokeTest
{
    [Fact]
    public void TestRunner_Discovers_This_Assembly()
    {
        Assert.True(true);
    }

    [Fact]
    public void Chronicle_Types_Are_Reachable_From_Tests()
    {
        var ruleType = typeof(RecurrenceRule);

        Assert.Equal("Chronicle.Models.Recurrence", ruleType.Namespace);
    }
}
