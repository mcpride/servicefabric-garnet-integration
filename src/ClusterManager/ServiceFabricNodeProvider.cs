using System.Fabric;
using System.Fabric.Query;
using System.Text.Json;

namespace ClusterManager;

/// <summary>
/// Discovers Garnet node endpoints by enumerating the ready instances of the
/// GarnetService via the Service Fabric Naming/Query APIs and parsing the address
/// each instance published from its communication listener.
/// </summary>
internal sealed class ServiceFabricNodeProvider : IGarnetNodeProvider
{
    private readonly FabricClient _fabricClient;
    private readonly Uri _garnetServiceUri;
    private readonly string _endpointName;
    private readonly ILogger _logger;

    public ServiceFabricNodeProvider(
        FabricClient fabricClient,
        Uri garnetServiceUri,
        string endpointName,
        ILogger logger)
    {
        _fabricClient = fabricClient;
        _garnetServiceUri = garnetServiceUri;
        _endpointName = endpointName;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GarnetNode>> GetNodesAsync(CancellationToken cancellationToken)
    {
        var nodes = new List<GarnetNode>();

        var partitions = await _fabricClient.QueryManager
            .GetPartitionListAsync(_garnetServiceUri)
            .ConfigureAwait(false);

        foreach (var partition in partitions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var replicas = await _fabricClient.QueryManager
                .GetReplicaListAsync(partition.PartitionInformation.Id)
                .ConfigureAwait(false);

            foreach (var replica in replicas)
            {
                if (replica.ReplicaStatus != ServiceReplicaStatus.Ready)
                {
                    continue;
                }

                if (TryParseEndpoint(replica.ReplicaAddress, out var node))
                {
                    nodes.Add(node);
                }
            }
        }

        return nodes
            .DistinctBy(n => n.EndPoint)
            .OrderBy(n => n.Ip, StringComparer.Ordinal)
            .ThenBy(n => n.Port)
            .ToList();
    }

    private bool TryParseEndpoint(string? replicaAddress, out GarnetNode node)
    {
        node = null!;
        if (string.IsNullOrWhiteSpace(replicaAddress))
        {
            return false;
        }

        var raw = replicaAddress.Trim();

        // Named listeners publish JSON: {"Endpoints":{"GarnetEndpoint":"ip:port"}}.
        if (raw.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(raw);
                if (document.RootElement.TryGetProperty("Endpoints", out var endpoints))
                {
                    if (endpoints.TryGetProperty(_endpointName, out var named))
                    {
                        raw = named.GetString() ?? string.Empty;
                    }
                    else
                    {
                        raw = endpoints.EnumerateObject().FirstOrDefault().Value.GetString() ?? string.Empty;
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Could not parse replica address '{Address}'", replicaAddress);
                return false;
            }
        }

        return TryParseIpPort(raw, out node);
    }

    private static bool TryParseIpPort(string value, out GarnetNode node)
    {
        node = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
        {
            return false;
        }

        var ip = value[..separator];
        if (!int.TryParse(value[(separator + 1)..], out var port))
        {
            return false;
        }

        node = new GarnetNode(ip, port);
        return true;
    }
}
