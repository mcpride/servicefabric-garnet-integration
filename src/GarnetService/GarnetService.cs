using System.Fabric;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;

namespace GarnetService;

/// <summary>
/// Stateless Service Fabric service that hosts a single in-process Microsoft Garnet
/// server instance (one Garnet node per Service Fabric node).
/// </summary>
internal sealed class GarnetService : StatelessService
{
    public GarnetService(StatelessServiceContext context)
        : base(context)
    {
    }

    /// <summary>
    /// Exposes the embedded Garnet RESP/Redis endpoint to the cluster through a
    /// custom communication listener. The listener name ("GarnetEndpoint") is the
    /// key under which the published address can be resolved via the Naming Service.
    /// </summary>
    protected override IEnumerable<ServiceInstanceListener> CreateServiceInstanceListeners()
    {
        yield return new ServiceInstanceListener(
            serviceContext => new GarnetCommunicationListener(serviceContext),
            name: "GarnetEndpoint");
    }
}
