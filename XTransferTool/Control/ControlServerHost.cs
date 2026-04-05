using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XTransferTool.Config;

namespace XTransferTool.Control;

public sealed class ControlServerHost : IAsyncDisposable
{
    private int _port;
    private readonly int _preferredPort;
    private readonly bool _fallbackToEphemeralPort;
    private readonly Inbox.IInboxRepository _inbox;
    private readonly SettingsStore? _settings;
    private IHost? _host;

    public ControlServerHost(int preferredPort, bool fallbackToEphemeralPort, Inbox.IInboxRepository inbox, SettingsStore? settings = null)
    {
        _preferredPort = preferredPort;
        _fallbackToEphemeralPort = fallbackToEphemeralPort;
        _port = preferredPort;
        _inbox = inbox;
        _settings = settings;
    }

    public int Port => _port;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_host is not null)
            return;

        _port = _preferredPort;
        try
        {
            _host = BuildHost(_port);
            await _host.StartAsync(ct);
            Console.WriteLine($"[control] gRPC listening on *:{_port} (HTTP/2); discovery should advertise this port");
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception) when (_fallbackToEphemeralPort)
        {
            // If fallback is enabled, be resilient: any startup failure on the preferred port
            // will retry with an ephemeral port. This avoids missing "address in use" variants
            // across platforms/runtime layers.
            try
            {
                _host?.Dispose();
            }
            catch
            {
                // ignore
            }
            _host = null;
        }
        catch (Exception ex) when (!_fallbackToEphemeralPort && IsAddressInUse(ex))
        {
            throw new IOException($"Failed to bind preferred port {_preferredPort} and fallback disabled.", ex);
        }

        _port = GetFreeTcpPort();
        _host = BuildHost(_port);
        await _host.StartAsync(ct);
        Console.WriteLine($"[control] gRPC listening on *:{_port} (ephemeral fallback; update firewall & peer expectations)");
    }

    private IHost BuildHost(int port)
    {
        _host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.ConfigureKestrel(kestrel =>
                {
                    kestrel.Listen(IPAddress.Any, port, lo => { lo.Protocols = HttpProtocols.Http2; });
                });

                webBuilder.ConfigureServices(services =>
                {
                    services.AddGrpc();
                    services.AddSingleton<InMemoryEventBus>();
                    services.AddSingleton<XTransferTool.Transfer.TransferStore>();
                    services.AddSingleton<XTransferTool.Transfer.UploadStateStore>();
                    services.AddSingleton(_inbox);
                    if (_settings is not null)
                        services.AddSingleton(_settings);
                    services.AddSingleton<XTransferTool.Remote.RemoteSessionStore>();
                });

                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGrpcService<ControlServiceImpl>();
                        endpoints.MapGrpcService<MessagingServiceImpl>();
                        endpoints.MapGrpcService<XTransferTool.Transfer.FileTransferServiceImpl>();
                        endpoints.MapGrpcService<XTransferTool.Remote.RemoteControlServiceImpl>();
                    });
                });
            })
            .Build();
        return _host;
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool IsAddressInUse(Exception ex)
    {
        if (ex is Microsoft.AspNetCore.Connections.AddressInUseException)
            return true;

        if (ex is SocketException se && se.SocketErrorCode == SocketError.AddressAlreadyInUse)
            return true;

        if (ex is AggregateException ae)
        {
            foreach (var inner in ae.InnerExceptions)
            {
                if (IsAddressInUse(inner))
                    return true;
            }
        }

        if (ex is IOException && ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
            return true;

        if (ex.InnerException is not null)
            return IsAddressInUse(ex.InnerException);

        return false;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_host is null)
            return;

        await _host.StopAsync(ct);
        _host.Dispose();
        _host = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

