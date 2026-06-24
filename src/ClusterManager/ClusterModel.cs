namespace ClusterManager;

/// <summary>A reachable Garnet RESP endpoint (ip:port).</summary>
internal sealed record GarnetNode(string Ip, int Port)
{
    public string EndPoint => $"{Ip}:{Port}";
}

/// <summary>One parsed line of the <c>CLUSTER NODES</c> output.</summary>
internal sealed record ClusterNodeInfo(
    string Id,
    string EndPoint,
    bool IsMaster,
    bool IsReplica,
    bool IsFailed,
    bool IsSuspected,
    string MasterId,
    bool HasSlots)
{
    /// <summary>
    /// The cluster reached consensus that this node is down (<c>fail</c>) or a peer
    /// merely suspects it (<c>fail?</c>/<c>pfail</c>). Only the former is authoritative.
    /// </summary>
    public bool IsFailing => IsFailed || IsSuspected;

    /// <summary>Neither agreed-failed nor suspected — a safe failover target.</summary>
    public bool IsHealthy => !IsFailed && !IsSuspected;
}

/// <summary>
/// Supplies the set of Garnet nodes that currently make up the cache. The Service
/// Fabric implementation resolves this from the Naming Service; tests can supply a
/// static list.
/// </summary>
internal interface IGarnetNodeProvider
{
    Task<IReadOnlyList<GarnetNode>> GetNodesAsync(CancellationToken cancellationToken);
}
