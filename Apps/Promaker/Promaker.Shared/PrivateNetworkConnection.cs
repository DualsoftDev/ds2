using System.Net;
using System.Net.Sockets;

namespace Promaker.Shared;

/// <summary>Resolves and connects only to private or loopback peers.</summary>
internal static class PrivateNetworkConnection
{
    internal static async ValueTask<IPAddress> ResolveAddressAsync(
        string host,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
            addresses = [literal];
        else
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        var address = addresses.FirstOrDefault(AgentTransferSecurityOptions.IsPrivateOrLoopbackAddress);
        return address ?? throw new InvalidOperationException(
            $"Plaintext field endpoint '{host}' did not resolve to a private network address.");
    }

    internal static async ValueTask<Stream> ConnectTcpAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        var address = await ResolveAddressAsync(host, cancellationToken).ConfigureAwait(false);
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
