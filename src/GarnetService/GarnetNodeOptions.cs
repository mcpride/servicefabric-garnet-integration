using System.Fabric;
using System.Globalization;

namespace GarnetService;

/// <summary>
/// Strongly typed view over the "Garnet" configuration section plus the values that
/// Service Fabric provides at runtime (endpoint port, node IP, work directory).
/// Knows how to translate itself into Garnet command line arguments.
/// </summary>
internal sealed class GarnetNodeOptions
{
    public const string ConfigPackageName = "Config";
    public const string SectionName = "Garnet";
    public const string EndpointName = "GarnetEndpoint";

    public required string Mode { get; init; }

    public required int Port { get; init; }

    public required string NodeIp { get; init; }

    public required string CheckpointDir { get; init; }

    public string? MemorySize { get; init; }

    public string? IndexSize { get; init; }

    public bool EnableAof { get; init; }

    public bool CleanClusterConfig { get; init; }

    public string? ClusterUsername { get; init; }

    public string? ClusterPassword { get; init; }

    public string? ExtraArgs { get; init; }

    public bool ClusterEnabled =>
        string.Equals(Mode, "Cluster", StringComparison.OrdinalIgnoreCase);

    public static GarnetNodeOptions FromContext(StatelessServiceContext context)
    {
        var activation = context.CodePackageActivationContext;
        var config = activation.GetConfigurationPackageObject(ConfigPackageName);
        var section = config.Settings.Sections[SectionName];

        string GetString(string key, string fallback = "")
            => section.Parameters.Contains(key) ? section.Parameters[key].Value : fallback;

        bool GetBool(string key, bool fallback = false)
            => bool.TryParse(GetString(key, fallback.ToString()), out var value) ? value : fallback;

        var endpoint = activation.GetEndpoint(EndpointName);

        // Garnet stores its checkpoint and cluster configuration under the SF work
        // directory; isolate it per node so a single dev box can host several nodes.
        var checkpointDir = Path.Combine(
            activation.WorkDirectory,
            "garnet",
            Sanitize(context.NodeContext.NodeName));

        return new GarnetNodeOptions
        {
            Mode = GetString("Mode", "Standalone"),
            Port = endpoint.Port,
            NodeIp = context.NodeContext.IPAddressOrFQDN,
            CheckpointDir = checkpointDir,
            MemorySize = NullIfEmpty(GetString("MemorySize")),
            IndexSize = NullIfEmpty(GetString("IndexSize")),
            EnableAof = GetBool("EnableAof"),
            CleanClusterConfig = GetBool("CleanClusterConfig", true),
            ClusterUsername = NullIfEmpty(GetString("ClusterUsername")),
            ClusterPassword = NullIfEmpty(GetString("ClusterPassword")),
            ExtraArgs = NullIfEmpty(GetString("ExtraArgs")),
        };
    }

    /// <summary>
    /// Builds the Garnet command line. Garnet binds to all interfaces by default and,
    /// in cluster mode, advertises the node IP so that MOVED/ASK redirects sent to
    /// Redis cluster clients point at a reachable address.
    /// </summary>
    public string[] ToGarnetArgs()
    {
        var args = new List<string>
        {
            "--port", Port.ToString(CultureInfo.InvariantCulture),
            "--checkpointdir", CheckpointDir,
        };

        if (!string.IsNullOrWhiteSpace(MemorySize))
        {
            args.Add("--memory");
            args.Add(MemorySize!);
        }

        if (!string.IsNullOrWhiteSpace(IndexSize))
        {
            args.Add("--index");
            args.Add(IndexSize!);
        }

        if (EnableAof)
        {
            args.Add("--aof");
        }

        if (ClusterEnabled)
        {
            args.Add("--cluster");
            args.Add("--cluster-announce-ip");
            args.Add(NodeIp);
            args.Add("--cluster-announce-port");
            args.Add(Port.ToString(CultureInfo.InvariantCulture));

            if (CleanClusterConfig)
            {
                // Start every node with an empty cluster config; the ClusterManager
                // (control plane) is the single authority that (re)forms the cluster.
                args.Add("--clean-cluster-config");
            }

            if (!string.IsNullOrWhiteSpace(ClusterUsername))
            {
                args.Add("--cluster-username");
                args.Add(ClusterUsername!);
            }

            if (!string.IsNullOrWhiteSpace(ClusterPassword))
            {
                args.Add("--cluster-password");
                args.Add(ClusterPassword!);
            }
        }

        if (!string.IsNullOrWhiteSpace(ExtraArgs))
        {
            foreach (var token in ExtraArgs!.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                args.Add(token);
            }
        }

        return args.ToArray();
    }

    private static string? NullIfEmpty(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Sanitize(string nodeName)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            nodeName = nodeName.Replace(c, '_');
        }

        return nodeName;
    }
}
