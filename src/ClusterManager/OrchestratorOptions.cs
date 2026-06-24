namespace ClusterManager;

/// <summary>Tunable parameters for <see cref="GarnetClusterOrchestrator"/>.</summary>
internal sealed class OrchestratorOptions
{
    /// <summary>Number of replicas to attach to every primary (0 = no replicas).</summary>
    public int ReplicaCount { get; init; } = 1;

    public string? Username { get; init; }

    public string? Password { get; init; }

    public int ConnectTimeoutMs { get; init; } = 5000;

    public int ConvergenceTimeoutMs { get; init; } = 30000;

    /// <summary>
    /// How many consecutive reconcile cycles a *suspected* (not yet cluster-agreed)
    /// primary must stay unreachable from the control plane before a failover is forced.
    /// Cluster-agreed failures (<c>fail</c>) act immediately. Minimum 1.
    /// </summary>
    public int FailoverConfirmations { get; init; } = 2;

    /// <summary>
    /// Minimum time between forced failovers of the same primary, so an in-flight
    /// promotion is given time to settle instead of being retriggered every cycle.
    /// </summary>
    public int FailoverCooldownMs { get; init; } = 30000;

    /// <summary>Total number of hash slots in a Garnet/Redis cluster.</summary>
    public const int TotalSlots = 16384;
}
