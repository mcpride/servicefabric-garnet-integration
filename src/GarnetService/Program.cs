using Microsoft.ServiceFabric.Services.Runtime;

namespace GarnetService;

/// <summary>
/// Host process entry point. Registers the stateless service type that wraps an
/// embedded Microsoft Garnet server with the Service Fabric runtime.
/// </summary>
internal static class Program
{
    private static void Main()
    {
        try
        {
            // The service type name must match the one declared in ServiceManifest.xml.
            ServiceRuntime.RegisterServiceAsync(
                    "GarnetServiceType",
                    context => new GarnetService(context))
                .GetAwaiter()
                .GetResult();

            // Keep the host process alive so the registered service keeps running.
            Thread.Sleep(Timeout.Infinite);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GarnetService] Host terminated unexpectedly: {ex}");
            throw;
        }
    }
}
