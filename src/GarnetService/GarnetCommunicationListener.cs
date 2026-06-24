using System.Fabric;
using System.Globalization;
using Garnet;
using Microsoft.ServiceFabric.Services.Communication.Runtime;

namespace GarnetService;

/// <summary>
/// Service Fabric communication listener that owns the lifetime of an embedded
/// <see cref="GarnetServer"/>. <c>OpenAsync</c> starts the server and publishes its
/// "ip:port" address to the Naming Service; <c>CloseAsync</c>/<c>Abort</c> dispose it.
/// </summary>
internal sealed class GarnetCommunicationListener : ICommunicationListener
{
    private readonly StatelessServiceContext _context;
    private readonly ILoggerFactory _loggerFactory;
    private GarnetServer? _server;

    public GarnetCommunicationListener(StatelessServiceContext context)
    {
        _context = context;
        _loggerFactory = LoggerFactory.Create(builder => builder
            .AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            })
            .SetMinimumLevel(LogLevel.Information));
    }

    public Task<string> OpenAsync(CancellationToken cancellationToken)
    {
        var options = GarnetNodeOptions.FromContext(_context);
        Directory.CreateDirectory(options.CheckpointDir);

        var logger = _loggerFactory.CreateLogger("GarnetService");
        logger.LogInformation(
            "Starting Garnet node {NodeIp}:{Port} (mode={Mode}, aof={Aof})",
            options.NodeIp,
            options.Port,
            options.Mode,
            options.EnableAof);

        // GarnetServer.Start() is non-blocking: it spins up the network listeners on
        // background threads and returns, so it is safe to call from OpenAsync.
        var server = new GarnetServer(options.ToGarnetArgs(), _loggerFactory);
        server.Start();
        _server = server;

        var address = string.Format(
            CultureInfo.InvariantCulture, "{0}:{1}", options.NodeIp, options.Port);
        return Task.FromResult(address);
    }

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        Stop();
        return Task.CompletedTask;
    }

    public void Abort() => Stop();

    private void Stop()
    {
        try
        {
            _server?.Dispose();
        }
        catch
        {
            // Best effort: never let teardown failures crash the host process.
        }
        finally
        {
            _server = null;
        }
    }
}
