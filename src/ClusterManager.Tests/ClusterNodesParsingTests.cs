using Xunit;

namespace ClusterManager.Tests;

/// <summary>
/// Tests for <see cref="GarnetClusterOrchestrator.ParseClusterNodes"/>. The most
/// important behaviour is the distinction between an agreed cluster failure
/// (<c>fail</c>) and a single peer's subjective suspicion (<c>fail?</c>/<c>pfail</c>),
/// because the hardened failover acts immediately on the former but only after
/// independent confirmation on the latter.
/// </summary>
public class ClusterNodesParsingTests
{
    private const string Sample =
        "id1 10.0.0.1:6379@16379 myself,master - 0 0 1 connected 0-5460\n" +
        "id2 10.0.0.2:6379@16379 master - 0 0 2 connected 5461-10922\n" +
        "id3 10.0.0.3:6379@16379 master,fail - 0 0 3 disconnected 10923-16383\n" +
        "id5 10.0.0.5:6379@16379 master,fail? - 0 0 5 disconnected 200-300\n" +
        "id4 10.0.0.4:6379@16379 slave id3 0 0 4 connected\n";

    private static ClusterNodeInfo Node(string id)
        => GarnetClusterOrchestrator.ParseClusterNodes(Sample).Single(n => n.Id == id);

    [Fact]
    public void ParsesEveryWellFormedLine()
    {
        Assert.Equal(5, GarnetClusterOrchestrator.ParseClusterNodes(Sample).Count);
    }

    [Fact]
    public void AgreedFail_IsFailed_NotSuspected()
    {
        var node = Node("id3");

        Assert.True(node.IsFailed);
        Assert.False(node.IsSuspected);
        Assert.True(node.IsFailing);
        Assert.False(node.IsHealthy);
    }

    [Fact]
    public void SubjectiveSuspicion_IsSuspected_NotFailed()
    {
        // "fail?" (PFAIL) must NOT be treated as an agreed failure.
        var node = Node("id5");

        Assert.True(node.IsSuspected);
        Assert.False(node.IsFailed);
        Assert.True(node.IsFailing);
        Assert.False(node.IsHealthy);
    }

    [Fact]
    public void HealthyMaster_IsHealthyAndOwnsSlots()
    {
        var node = Node("id1");

        Assert.True(node.IsMaster);
        Assert.True(node.IsHealthy);
        Assert.True(node.HasSlots);
        Assert.False(node.IsFailing);
    }

    [Fact]
    public void Replica_IsDetectedWithItsMasterId()
    {
        var node = Node("id4");

        Assert.True(node.IsReplica);
        Assert.False(node.IsMaster);
        Assert.Equal("id3", node.MasterId);
        Assert.False(node.HasSlots);
    }

    [Theory]
    [InlineData("slave")]
    [InlineData("replica")]
    public void Replica_RecognisesBothSlaveAndReplicaFlags(string flag)
    {
        var line = $"r1 10.0.0.9:6379@16379 {flag} m1 0 0 9 connected";

        var node = GarnetClusterOrchestrator.ParseClusterNodes(line).Single();

        Assert.True(node.IsReplica);
        Assert.Equal("m1", node.MasterId);
    }

    [Fact]
    public void Endpoint_StripsBusPortAndHostname()
    {
        var line = "n1 10.0.0.7:6379@16379,host.example 0 0 0 1 connected 0-100";

        var node = GarnetClusterOrchestrator.ParseClusterNodes(line).Single();

        Assert.Equal("10.0.0.7:6379", node.EndPoint);
    }

    [Fact]
    public void MasterId_DashBecomesEmpty()
    {
        Assert.Equal(string.Empty, Node("id1").MasterId);
    }

    [Fact]
    public void SkipsLinesWithTooFewFields()
    {
        var text = "good 10.0.0.1:6379@16379 master - 0 0 1 connected 0-1\nbad too few fields\n";

        var parsed = GarnetClusterOrchestrator.ParseClusterNodes(text);

        Assert.Equal("good", Assert.Single(parsed).Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    public void EmptyInput_ReturnsEmptyList(string text)
    {
        Assert.Empty(GarnetClusterOrchestrator.ParseClusterNodes(text));
    }

    [Fact]
    public void CountKnownNodes_CountsParsedEntries()
    {
        Assert.Equal(5, GarnetClusterOrchestrator.CountKnownNodes(Sample));
    }

    [Fact]
    public void HasAssignedSlots_TrueWhenAnyNodeOwnsSlots()
    {
        Assert.True(GarnetClusterOrchestrator.HasAssignedSlots(Sample));
        Assert.False(GarnetClusterOrchestrator.HasAssignedSlots(
            "lonely 10.0.0.1:6379@16379 myself,master - 0 0 0 connected"));
    }
}
