using System.Globalization;

namespace ClusterManager;

/// <summary>
/// The Garnet cluster control plane. On every reconcile cycle it discovers the live
/// Garnet nodes and either:
/// <list type="bullet">
///   <item>forms a brand new cluster (assign config epochs, shard the 16384 slots
///   across primaries, gossip the nodes together, attach replicas), or</item>
///   <item>reconciles an existing cluster (introduce newly added nodes and force a
///   replica failover when a primary is reported as failing).</item>
/// </list>
/// All steps are best-effort and idempotent across cycles: Garnet's own gossip and
/// Service Fabric's instance restarts do most of the heavy lifting.
/// </summary>
internal sealed class GarnetClusterOrchestrator
{
    private readonly IGarnetNodeProvider _nodeProvider;
    private readonly OrchestratorOptions _options;
    private readonly ILogger _logger;

    // Failover state that must survive across reconcile cycles. The control loop is
    // single-threaded, so plain dictionaries are sufficient (keyed by node id).
    private readonly Dictionary<string, int> _suspectStrikes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastFailoverUtc = new(StringComparer.OrdinalIgnoreCase);

    public GarnetClusterOrchestrator(
        IGarnetNodeProvider nodeProvider,
        OrchestratorOptions options,
        ILogger logger)
    {
        _nodeProvider = nodeProvider;
        _options = options;
        _logger = logger;
    }

    public async Task ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        var nodes = await _nodeProvider.GetNodesAsync(cancellationToken).ConfigureAwait(false);
        if (nodes.Count == 0)
        {
            _logger.LogInformation("No Garnet nodes discovered yet; waiting");
            return;
        }

        var inspection = Inspect(nodes);
        if (inspection.Reachable.Count == 0)
        {
            _logger.LogWarning("Discovered {Count} node(s) but none are reachable yet", nodes.Count);
            return;
        }

        if (!inspection.AnyFormed || inspection.Source is null)
        {
            FormCluster(inspection.Reachable, cancellationToken);
        }
        else
        {
            Reconcile(nodes, inspection.Topology, inspection.Source, cancellationToken);
        }
    }

    private readonly record struct Inspection(
        List<GarnetNode> Reachable,
        bool AnyFormed,
        IReadOnlyList<ClusterNodeInfo> Topology,
        GarnetNode? Source);

    private Inspection Inspect(IReadOnlyList<GarnetNode> nodes)
    {
        var reachable = new List<GarnetNode>();
        var anyFormed = false;
        IReadOnlyList<ClusterNodeInfo> bestView = Array.Empty<ClusterNodeInfo>();
        GarnetNode? source = null;
        var bestKnown = -1;

        foreach (var node in nodes)
        {
            try
            {
                using var client = Connect(node);
                reachable.Add(node);

                // NOTE: a brand new, isolated Garnet node already reports
                // "cluster_state:ok", so that flag must NOT be used to detect an
                // existing cluster. The real signals of a formed cluster are that a
                // node knows more than itself, or that it already owns slots.
                var parsed = ParseClusterNodes(client.Execute("CLUSTER", "NODES").AsString());
                if (parsed.Count > 1 || parsed.Any(p => p.HasSlots))
                {
                    anyFormed = true;
                }

                // Drive every topology decision from the most-informed reachable node
                // rather than from an arbitrary one whose gossip view may be stale.
                if (parsed.Count > bestKnown)
                {
                    bestKnown = parsed.Count;
                    bestView = parsed;
                    source = node;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Garnet node {Endpoint} is not reachable yet", node.EndPoint);
            }
        }

        return new Inspection(reachable, anyFormed, bestView, source);
    }

    private void FormCluster(List<GarnetNode> live, CancellationToken cancellationToken)
    {
        var n = live.Count;
        var replicasPerPrimary = Math.Max(0, _options.ReplicaCount);
        var primaryCount = replicasPerPrimary == 0
            ? n
            : Math.Max(1, n / (replicasPerPrimary + 1));

        _logger.LogInformation(
            "Forming Garnet cluster across {Nodes} node(s): {Primaries} primary/primaries, {Replicas} replica(s) per primary",
            n,
            primaryCount,
            replicasPerPrimary);

        // 1. Assign a unique config epoch to each node while it still only knows itself.
        for (var i = 0; i < n; i++)
        {
            TryExec(live[i], "set-config-epoch", "CLUSTER", "SET-CONFIG-EPOCH", Num(i + 1));
        }

        // 2. Give every primary a disjoint, contiguous slot range.
        var ranges = DistributeSlots(primaryCount);
        for (var i = 0; i < primaryCount; i++)
        {
            TryExec(live[i], "addslotsrange", "CLUSTER", "ADDSLOTSRANGE", Num(ranges[i].Start), Num(ranges[i].End));
        }

        // 3. Introduce every node to the first one; gossip propagates the rest.
        for (var i = 1; i < n; i++)
        {
            TryExec(live[0], "meet", "CLUSTER", "MEET", live[i].Ip, Num(live[i].Port));
        }

        // 4. Wait until every node has heard about all the others.
        WaitForConvergence(live, n, cancellationToken);

        // 5. Capture the primary node ids, then attach the remaining nodes as replicas.
        var primaryIds = new string[primaryCount];
        for (var i = 0; i < primaryCount; i++)
        {
            primaryIds[i] = ReadMyId(live[i]);
        }

        for (var j = primaryCount; j < n; j++)
        {
            var primaryIndex = (j - primaryCount) % primaryCount;
            var primaryId = primaryIds[primaryIndex];
            if (string.IsNullOrEmpty(primaryId))
            {
                _logger.LogWarning("Skipping replica {Endpoint}: primary id unknown", live[j].EndPoint);
                continue;
            }

            TryExec(live[j], "replicate", "CLUSTER", "REPLICATE", primaryId);
        }

        // 6. Wait for the cluster to report a healthy state.
        WaitForClusterOk(live, cancellationToken);
    }

    private void Reconcile(
        IReadOnlyList<GarnetNode> discovered,
        IReadOnlyList<ClusterNodeInfo> topology,
        GarnetNode source,
        CancellationToken cancellationToken)
    {
        var known = topology.Select(p => p.EndPoint).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Introduce any newly added Service Fabric instance to the cluster.
        foreach (var node in discovered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!known.Contains(node.EndPoint))
            {
                _logger.LogInformation("Meeting newly discovered Garnet node {Endpoint}", node.EndPoint);
                TryExec(source, "meet", "CLUSTER", "MEET", node.Ip, Num(node.Port));
            }
        }

        HealFailingPrimaries(topology, cancellationToken);
        PruneFailoverState(topology);
    }

    private void HealFailingPrimaries(IReadOnlyList<ClusterNodeInfo> topology, CancellationToken cancellationToken)
    {
        // Only primaries that still own slots need rescuing. Once a replica has been
        // promoted the dead primary keeps its "fail" flag but loses its slots, so this
        // guard also stops us from re-promoting on every subsequent cycle.
        var failing = topology.Where(p => p.IsMaster && p.HasSlots && p.IsFailing).ToList();

        foreach (var master in failing)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!ShouldFailover(master))
            {
                continue;
            }

            if (IsInFailoverCooldown(master.Id))
            {
                _logger.LogInformation(
                    "Skipping failover for primary {Endpoint}: a previous promotion is still settling",
                    master.EndPoint);
                continue;
            }

            var replica = SelectBestReplica(topology, master);
            if (replica is null)
            {
                _logger.LogWarning(
                    "Primary {Endpoint} is failing but no healthy replica is available to promote",
                    master.EndPoint);
                continue;
            }

            if (!TrySplitEndpoint(replica.EndPoint, out var replicaNode))
            {
                _logger.LogWarning("Cannot parse replica endpoint '{Endpoint}'; skipping failover", replica.EndPoint);
                continue;
            }

            _logger.LogWarning(
                "Failover: promoting replica {Replica} to replace {State} primary {Master}",
                replica.EndPoint,
                master.IsFailed ? "failed" : "unreachable",
                master.EndPoint);

            TryExec(replicaNode, "failover", "CLUSTER", "FAILOVER", "FORCE");
            _lastFailoverUtc[master.Id] = DateTime.UtcNow;
            _suspectStrikes.Remove(master.Id);
        }
    }

    /// <summary>
    /// Decides whether a failing primary should actually be failed over. Cluster-agreed
    /// failures act immediately; a mere suspicion (PFAIL) is confirmed independently by
    /// the control plane and must persist for several cycles to defend against transient
    /// gossip blips (a GC pause or a brief network partition) causing needless promotions.
    /// </summary>
    private bool ShouldFailover(ClusterNodeInfo master)
    {
        if (master.IsFailed)
        {
            _suspectStrikes.Remove(master.Id);
            return true;
        }

        // Suspected only: if the control plane can still reach the primary, the suspicion
        // is almost certainly a false alarm, so cancel any pending failover for it.
        if (IsReachable(master.EndPoint))
        {
            if (_suspectStrikes.Remove(master.Id))
            {
                _logger.LogInformation(
                    "Primary {Endpoint} is reachable again; cancelling the pending failover",
                    master.EndPoint);
            }

            return false;
        }

        var strikes = _suspectStrikes.GetValueOrDefault(master.Id) + 1;
        _suspectStrikes[master.Id] = strikes;

        var required = Math.Max(1, _options.FailoverConfirmations);
        if (strikes < required)
        {
            _logger.LogInformation(
                "Primary {Endpoint} suspected down ({Strikes}/{Required} confirmations); waiting",
                master.EndPoint,
                strikes,
                required);
            return false;
        }

        return true;
    }

    private bool IsInFailoverCooldown(string masterId)
        => _lastFailoverUtc.TryGetValue(masterId, out var last)
           && (DateTime.UtcNow - last).TotalMilliseconds < _options.FailoverCooldownMs;

    /// <summary>Returns true if the control plane can open an authenticated connection and PING the endpoint.</summary>
    private bool IsReachable(string endpoint)
    {
        if (!TrySplitEndpoint(endpoint, out var node))
        {
            // We cannot verify the endpoint, so conservatively assume it is alive rather
            // than risk an unnecessary failover on an address we cannot probe.
            return true;
        }

        try
        {
            using var client = Connect(node);
            return !client.Execute("PING").IsError;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Picks the healthy replica of <paramref name="master"/> with the highest replication
    /// offset (least data loss). Falls back to the first candidate when offsets cannot be read.
    /// </summary>
    private ClusterNodeInfo? SelectBestReplica(IReadOnlyList<ClusterNodeInfo> topology, ClusterNodeInfo master)
    {
        var candidates = topology
            .Where(p => p.IsReplica
                        && p.IsHealthy
                        && string.Equals(p.MasterId, master.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count <= 1)
        {
            return candidates.FirstOrDefault();
        }

        ClusterNodeInfo? best = null;
        var bestOffset = long.MinValue;
        foreach (var candidate in candidates)
        {
            var offset = ReadReplicationOffset(candidate.EndPoint);
            if (offset > bestOffset)
            {
                bestOffset = offset;
                best = candidate;
            }
        }

        return best ?? candidates[0];
    }

    private long ReadReplicationOffset(string endpoint)
    {
        if (!TrySplitEndpoint(endpoint, out var node))
        {
            return -1;
        }

        try
        {
            using var client = Connect(node);
            return ParseReplicationOffset(client.Execute("INFO", "replication").AsString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read replication offset from {Endpoint}", endpoint);
            return -1;
        }
    }

    /// <summary>Forgets per-primary failover bookkeeping that is no longer relevant.</summary>
    private void PruneFailoverState(IReadOnlyList<ClusterNodeInfo> topology)
    {
        var live = topology.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stillSuspect = topology
            .Where(p => p.IsMaster && p.IsFailing)
            .Select(p => p.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var id in _suspectStrikes.Keys.ToList())
        {
            if (!stillSuspect.Contains(id))
            {
                _suspectStrikes.Remove(id);
            }
        }

        foreach (var entry in _lastFailoverUtc.ToList())
        {
            var expired = (DateTime.UtcNow - entry.Value).TotalMilliseconds >= _options.FailoverCooldownMs;
            if (expired || !live.Contains(entry.Key))
            {
                _lastFailoverUtc.Remove(entry.Key);
            }
        }
    }

    private void WaitForConvergence(IReadOnlyList<GarnetNode> live, int expected, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(_options.ConvergenceTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var converged = true;
            foreach (var node in live)
            {
                try
                {
                    using var client = Connect(node);
                    if (CountKnownNodes(client.Execute("CLUSTER", "NODES").AsString()) < expected)
                    {
                        converged = false;
                        break;
                    }
                }
                catch
                {
                    converged = false;
                    break;
                }
            }

            if (converged)
            {
                _logger.LogInformation("All {Expected} nodes have converged on the cluster topology", expected);
                return;
            }

            Thread.Sleep(1000);
        }

        _logger.LogWarning("Timed out waiting for cluster convergence; will re-check next cycle");
    }

    private void WaitForClusterOk(IReadOnlyList<GarnetNode> live, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(_options.ConvergenceTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var client = Connect(live[0]);
                if (ClusterStateIsOk(client.Execute("CLUSTER", "INFO").AsString()))
                {
                    _logger.LogInformation("Garnet cluster is formed and reports state: ok");
                    return;
                }
            }
            catch
            {
                // retry until the deadline
            }

            Thread.Sleep(1000);
        }

        _logger.LogWarning("Cluster did not reach state 'ok' within the timeout; will re-check next cycle");
    }

    private RespClient Connect(GarnetNode node)
    {
        var client = new RespClient(node.Ip, node.Port, _options.ConnectTimeoutMs);
        client.Authenticate(_options.Username, _options.Password);
        return client;
    }

    private string ReadMyId(GarnetNode node)
    {
        try
        {
            using var client = Connect(node);
            return client.Execute("CLUSTER", "MYID").AsString().Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read CLUSTER MYID from {Endpoint}", node.EndPoint);
            return string.Empty;
        }
    }

    private void TryExec(GarnetNode node, string operation, params string[] command)
    {
        try
        {
            using var client = Connect(node);
            var reply = client.Execute(command);
            if (reply.IsError)
            {
                _logger.LogWarning(
                    "CLUSTER {Operation} on {Endpoint} returned: {Error}",
                    operation,
                    node.EndPoint,
                    reply.Text);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CLUSTER {Operation} on {Endpoint} failed", operation, node.EndPoint);
        }
    }

    internal static IReadOnlyList<(int Start, int End)> DistributeSlots(int primaryCount)
    {
        var ranges = new List<(int Start, int End)>(primaryCount);
        var baseSize = OrchestratorOptions.TotalSlots / primaryCount;
        var remainder = OrchestratorOptions.TotalSlots % primaryCount;

        var start = 0;
        for (var i = 0; i < primaryCount; i++)
        {
            var size = baseSize + (i < remainder ? 1 : 0);
            var end = start + size - 1;
            ranges.Add((start, end));
            start = end + 1;
        }

        return ranges;
    }

    internal static IReadOnlyList<ClusterNodeInfo> ParseClusterNodes(string text)
    {
        var result = new List<ClusterNodeInfo>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 8)
            {
                continue;
            }

            var id = fields[0];
            var endpoint = StripBusAndHostname(fields[1]);
            var flags = fields[2].Split(',', StringSplitOptions.RemoveEmptyEntries);
            var masterId = fields[3] == "-" ? string.Empty : fields[3];

            var isMaster = flags.Contains("master");
            var isReplica = flags.Contains("slave") || flags.Contains("replica");
            var isFailed = flags.Contains("fail");
            var isSuspected = flags.Contains("fail?") || flags.Contains("pfail");
            var hasSlots = fields.Length > 8;

            result.Add(new ClusterNodeInfo(id, endpoint, isMaster, isReplica, isFailed, isSuspected, masterId, hasSlots));
        }

        return result;
    }

    private static string StripBusAndHostname(string address)
    {
        // Format: ip:port@busport[,hostname]
        var at = address.IndexOf('@');
        return at > 0 ? address[..at] : address;
    }

    internal static int CountKnownNodes(string clusterNodesText)
        => ParseClusterNodes(clusterNodesText).Count;

    internal static bool HasAssignedSlots(string clusterNodesText)
        => ParseClusterNodes(clusterNodesText).Any(n => n.HasSlots);

    internal static bool ClusterStateIsOk(string clusterInfoText)
        => clusterInfoText.Contains("cluster_state:ok", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts a replication offset from <c>INFO replication</c> output, preferring the
    /// replica's applied offset (<c>slave_repl_offset</c>) and falling back to the link
    /// offset (<c>master_repl_offset</c>). Returns -1 when no offset can be parsed.
    /// </summary>
    internal static long ParseReplicationOffset(string infoReplication)
    {
        if (string.IsNullOrEmpty(infoReplication))
        {
            return -1;
        }

        foreach (var key in new[] { "slave_repl_offset:", "master_repl_offset:" })
        {
            var index = infoReplication.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var start = index + key.Length;
            var end = start;
            while (end < infoReplication.Length && char.IsDigit(infoReplication[end]))
            {
                end++;
            }

            if (end > start && long.TryParse(
                    infoReplication.AsSpan(start, end - start),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var offset))
            {
                return offset;
            }
        }

        return -1;
    }

    private static bool TrySplitEndpoint(string endpoint, out GarnetNode node)
    {
        node = null!;
        var separator = endpoint.LastIndexOf(':');
        if (separator <= 0 || separator == endpoint.Length - 1)
        {
            return false;
        }

        if (!int.TryParse(endpoint[(separator + 1)..], out var port))
        {
            return false;
        }

        node = new GarnetNode(endpoint[..separator], port);
        return true;
    }

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);
}
