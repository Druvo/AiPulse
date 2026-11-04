using AiPulse.Models;
using Xunit;

namespace AiPulse.Tests;

public class SeedSourceTests
{
    /// <summary>
    /// Reddit's anonymous RSS rate-limits hard (roughly half of requests returned HTTP 429 even at 37 subreddits), so a fresh install
    /// starts with a small core set switched on. The rest stay in the file, switched off, for anyone who wants to turn them on.
    /// </summary>
    [Fact]
    public void A_fresh_install_follows_only_a_small_core_set_of_subreddits_and_keeps_the_rest_switched_off()
    {
        var reddit = TestData.Load<FeedSource>("sources.json").Where(s => s.Url.Contains("reddit.com/r/")).ToList();

        Assert.True(reddit.Count > 50, "the larger list should stay available, switched off");
        var on = reddit.Where(s => s.Enabled).ToList();
        Assert.InRange(on.Count, 10, 20);
        Assert.Contains(on, s => s.Name == "r/LocalLLaMA");
    }
}
