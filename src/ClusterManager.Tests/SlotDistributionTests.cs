using Xunit;

namespace ClusterManager.Tests;

/// <summary>
/// Tests for <see cref="GarnetClusterOrchestrator.DistributeSlots"/>: the 16384 hash
/// slots must be split into disjoint, contiguous, near-equal ranges that fully cover
/// the keyspace, regardless of the primary count.
/// </summary>
public class SlotDistributionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(100)]
    [InlineData(OrchestratorOptions.TotalSlots)]
    public void ProducesContiguousNearEqualRangesCoveringAllSlots(int primaryCount)
    {
        var ranges = GarnetClusterOrchestrator.DistributeSlots(primaryCount).ToList();

        Assert.Equal(primaryCount, ranges.Count);
        Assert.Equal(0, ranges[0].Start);
        Assert.Equal(OrchestratorOptions.TotalSlots - 1, ranges[^1].End);

        for (var i = 1; i < ranges.Count; i++)
        {
            Assert.Equal(ranges[i - 1].End + 1, ranges[i].Start);
        }

        var sizes = ranges.Select(r => r.End - r.Start + 1).ToList();
        Assert.All(sizes, size => Assert.True(size >= 1));
        Assert.True(sizes.Max() - sizes.Min() <= 1, "ranges should differ in size by at most one slot");
        Assert.Equal(OrchestratorOptions.TotalSlots, sizes.Sum());
    }
}
