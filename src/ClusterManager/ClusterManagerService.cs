using System.Fabric;
using Microsoft.ServiceFabric.Services.Runtime;

namespace ClusterManager;

/// <summary>
/// Stateless singleton (InstanceCount = 1) that runs the Garnet cluster control loop.
/// In Standalone mode it stays idle; in Cluster mode it periodically reconciles the
/// Garnet cluster topology with the set of live GarnetService instances.
/// </summary>
internal sealed class ClusterManagerService : StatelessService
{
    private const string GarnetEndpointName = "GarnetEndpoint";

    public ClusterManagerService(StatelessServiceContext context)
        : base(context)
    {
    }

    protected override async Task RunAsync(CancellationToken cancellationToken)
    {
        using var loggerFactory = LoggerFactory.Create(builder => builder
            .AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            })
            .SetMinimumLevel(LogLevel.Information));

        var logger = loggerFactory.CreateLogger("ClusterManager");
        var config = ReadConfig();

        if (!string.Equals(config.Mode, "Cluster", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "Mode is '{Mode}' (not Cluster): the control plane is idle", config.Mode);
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return;
        }

        var applicationName = Context.CodePackageActivationContext.ApplicationName;
        var garnetServiceUri = new Uri($"{applicationName}/{config.GarnetServiceName}");

        using var fabricClient = new FabricClient();
        var nodeProvider = new ServiceFabricNodeProvider(
            fabricClient, garnetServiceUri, GarnetEndpointName, logger);

        var orchestrator = new GarnetClusterOrchestrator(
            nodeProvider,
            new OrchestratorOptions
            {
                ReplicaCount = config.ReplicaCount,
                FailoverConfirmations = config.FailoverConfirmations,
                FailoverCooldownMs = config.FailoverCooldownSeconds * 1000,
                Username = config.Username,
                Password = config.Password,
            },
            logger);

        logger.LogInformation(
            "Control plane started for {Service}. Reconcile interval: {Interval}s, replicas/primary: {Replicas}",
            garnetServiceUri,
            config.ReconcileIntervalSeconds,
            config.ReplicaCount);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await orchestrator.ReconcileOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reconcile cycle failed; retrying next interval");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(config.ReconcileIntervalSeconds), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private ClusterManagerConfig ReadConfig()
    {
        var section = Context.CodePackageActivationContext
            .GetConfigurationPackageObject("Config")
            .Settings
            .Sections["ClusterManager"];

        string Get(string key, string fallback = "")
            => section.Parameters.Contains(key) ? section.Parameters[key].Value : fallback;

        int GetInt(string key, int fallback)
            => int.TryParse(Get(key, fallback.ToString()), out var value) ? value : fallback;

        return new ClusterManagerConfig
        {
            Mode = Get("Mode", "Standalone"),
            GarnetServiceName = Get("GarnetServiceName", "GarnetService"),
            ReplicaCount = GetInt("ReplicaCount", 1),
            ReconcileIntervalSeconds = Math.Max(5, GetInt("ReconcileIntervalSeconds", 15)),
            FailoverConfirmations = Math.Max(1, GetInt("FailoverConfirmations", 2)),
            FailoverCooldownSeconds = Math.Max(5, GetInt("FailoverCooldownSeconds", 30)),
            Username = NullIfEmpty(Get("ClusterUsername")),
            Password = NullIfEmpty(Get("ClusterPassword")),
        };
    }

    private static string? NullIfEmpty(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed class ClusterManagerConfig
    {
        public required string Mode { get; init; }

        public required string GarnetServiceName { get; init; }

        public int ReplicaCount { get; init; }

        public int ReconcileIntervalSeconds { get; init; }

        public int FailoverConfirmations { get; init; }

        public int FailoverCooldownSeconds { get; init; }

        public string? Username { get; init; }

        public string? Password { get; init; }
    }
}
