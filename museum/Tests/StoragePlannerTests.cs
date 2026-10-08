// Run: dotnet test (see museum/Tests/README.md). Pure C#, no Unity needed.
using System;
using System.Collections.Generic;
using RHMuseum.Apps;
using Xunit;

public class StoragePlannerTests
{
    static readonly DateTime Now = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    const long GB = 1L << 30;

    static InstalledApp App(string p, double hoursAgo, float rating, long gb = 1) =>
        new InstalledApp { package = p, lastPlayedUtc = Now.AddHours(-hoursAgo), rating = rating, bytes = gb * GB };

    [Fact]
    public void NoEvictionWhenItFits() =>
        Assert.Empty(StoragePlanner.PlanEvictions(new[] { App("a", 100, 3) }, 2 * GB, 1 * GB, 4 * GB, new string[0], Now));

    [Fact]
    public void OldestGoesFirst()
    {
        var plan = StoragePlanner.PlanEvictions(new[] { App("new", 2, 3), App("old", 48, 3) }, 4 * GB, 1 * GB, 4 * GB, new string[0], Now);
        Assert.Equal(new List<string> { "old" }, plan);
    }

    [Fact]
    public void RatingWeighsAgainstAge()
    {
        // 5-star played 30 h ago scores 30*1/3 = 10; 1-star played 10 h ago scores 10*5/3 = 16.7 -> evict the 1-star.
        var plan = StoragePlanner.PlanEvictions(new[] { App("loved", 30, 5), App("meh", 10, 1) }, 4 * GB, 1 * GB, 4 * GB, new string[0], Now);
        Assert.Equal("meh", plan[0]);
    }

    [Fact]
    public void ProtectedAndRecentlyPlayedAreKept()
    {
        var apps = new[] { App("batch", 500, 1), App("justPlayed", 0.1, 1), App("other", 5, 3) };
        var plan = StoragePlanner.PlanEvictions(apps, 3 * GB, 1 * GB, 3 * GB, new[] { "batch" }, Now);
        Assert.Equal(new List<string> { "other" }, plan);
    }

    [Fact]
    public void EvictsSeveralUntilItFits()
    {
        var apps = new[] { App("a", 10, 3), App("b", 20, 3), App("c", 30, 3) };
        var plan = StoragePlanner.PlanEvictions(apps, 3 * GB, 2 * GB, 3 * GB, new string[0], Now);
        Assert.Equal(new List<string> { "c", "b" }, plan);
    }

    [Fact]
    public void ImpossibleReturnsNull() =>
        Assert.Null(StoragePlanner.PlanEvictions(new[] { App("a", 10, 3) }, 1 * GB, 10 * GB, 4 * GB, new string[0], Now));

    [Fact]
    public void UnknownRatingCountsAsThree() =>
        Assert.Equal(StoragePlanner.EvictScore(App("x", 9, 0), Now), StoragePlanner.EvictScore(App("y", 9, 3), Now));

    [Fact]
    public void PreloadPicksBestRatedThatFit()
    {
        var apps = new[] { ("big5", 3 * GB, 5f), ("small4", 1 * GB, 4f), ("small2", 1 * GB, 2f), ("mid3", 2 * GB, 3f) };
        var picked = StoragePlanner.PreloadSelection(apps, a => a.Item2, a => a.Item3, 0, 5 * GB);
        Assert.Equal(new[] { "big5", "small4", "small2" }, picked.ConvertAll(a => a.Item1));   // mid3 doesn't fit after
    }

    [Fact]
    public void DownloadNeedsHeadroom()
    {
        Assert.True(StoragePlanner.CanDownload(1 * GB, 5 * GB));
        Assert.False(StoragePlanner.CanDownload(1 * GB, 3 * GB));   // 1 GB apk + 1.6 GB installed + 1 GB reserve > 3 GB
    }
}
