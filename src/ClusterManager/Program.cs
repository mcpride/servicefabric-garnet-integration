using Microsoft.ServiceFabric.Services.Runtime;

namespace ClusterManager;

/// <summary>
/// Host process entry point for the cluster control plane. Garnet's cluster mode is
/// "passive": it never elects leaders or assigns slots on its own. This service is the
/// external control plane that forms and heals the Garnet cluster on top of the
/// orchestration guarantees provided by Service Fabric.
/// </summary>
internal static class Program
{
    private static void Main()
    {
        try
        {
            ServiceRuntime.RegisterServiceAsync(
                    "ClusterManagerType",
                    context => new ClusterManagerService(context))
                .GetAwaiter()
                .GetResult();

            Thread.Sleep(Timeout.Infinite);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ClusterManager] Host terminated unexpectedly: {ex}");
            throw;
        }
    }
}
