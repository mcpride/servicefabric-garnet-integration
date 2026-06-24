using Xunit;

namespace ClusterManager.Tests;

/// <summary>Tests for <see cref="GarnetClusterOrchestrator.ClusterStateIsOk"/>.</summary>
public class ClusterStateTests
{
    [Theory]
    [InlineData("cluster_enabled:1\r\ncluster_state:ok\r\ncluster_slots_assigned:16384\r\n", true)]
    [InlineData("CLUSTER_STATE:OK", true)]
    [InlineData("cluster_state:fail", false)]
    [InlineData("", false)]
    public void DetectsHealthyClusterState(string clusterInfo, bool expected)
    {
        Assert.Equal(expected, GarnetClusterOrchestrator.ClusterStateIsOk(clusterInfo));
    }
}
