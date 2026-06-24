using Xunit;

namespace ClusterManager.Tests;

/// <summary>
/// Tests for <see cref="GarnetClusterOrchestrator.ParseReplicationOffset"/>, which the
/// hardened failover uses to promote the most up-to-date replica (least data loss).
/// </summary>
public class ReplicationOffsetTests
{
    [Fact]
    public void PrefersSlaveOffsetOverMasterOffset()
    {
        const string info = "role:slave\r\nmaster_repl_offset:5\r\nslave_repl_offset:12345\r\n";

        Assert.Equal(12345, GarnetClusterOrchestrator.ParseReplicationOffset(info));
    }

    [Fact]
    public void FallsBackToMasterOffsetWhenSlaveOffsetAbsent()
    {
        const string info = "role:master\r\nmaster_repl_offset:999\r\nconnected_slaves:1\r\n";

        Assert.Equal(999, GarnetClusterOrchestrator.ParseReplicationOffset(info));
    }

    [Fact]
    public void IsCaseInsensitive()
    {
        Assert.Equal(42, GarnetClusterOrchestrator.ParseReplicationOffset("SLAVE_REPL_OFFSET:42\r\n"));
    }

    [Fact]
    public void StopsAtFirstNonDigit()
    {
        Assert.Equal(7, GarnetClusterOrchestrator.ParseReplicationOffset("slave_repl_offset:7 (lagging)\r\n"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("slave_repl_offset:")]
    [InlineData("slave_repl_offset:notanumber")]
    public void ReturnsMinusOneWhenNoOffsetIsPresent(string info)
    {
        Assert.Equal(-1, GarnetClusterOrchestrator.ParseReplicationOffset(info));
    }
}
