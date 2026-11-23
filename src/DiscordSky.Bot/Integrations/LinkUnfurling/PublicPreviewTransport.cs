using System.Net;
using System.Net.Sockets;

namespace DiscordSky.Bot.Integrations.LinkUnfurling;

internal sealed class PublicPreviewTransport(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private static readonly System.Net.IPNetwork[] Reserved = new[]
    {
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12",
        "192.0.0.0/24", "192.0.2.0/24", "192.31.196.0/24", "192.52.193.0/24", "192.88.99.0/24", "192.168.0.0/16",
        "192.175.48.0/24", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4",
        "168.63.129.16/32", "2001::/23", "2001:db8::/32", "2002::/16", "2620:4f:8000::/48", "3fff::/20",
    }.Select(System.Net.IPNetwork.Parse).ToArray();
    private static readonly System.Net.IPNetwork GlobalV6 = System.Net.IPNetwork.Parse("2000::/3");

    internal static HttpMessageHandler Create() => new PublicPreviewTransport(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5), PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 4,
        ConnectCallback = async (context, token) =>
        {
            var addresses = await ResolveAsync(context.DnsEndPoint.Host, Dns.GetHostAddressesAsync, token).ConfigureAwait(false);
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        },
    });

    internal static bool Allows(Uri uri) => uri.IsAbsoluteUri && uri.Scheme is "http" or "https" && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && (IPAddress.TryParse(uri.DnsSafeHost, out var ip) ? IsPublic(ip) :
            uri.HostNameType == UriHostNameType.Dns && uri.IdnHost.TrimEnd('.').Contains('.'));

    private static bool IsPublic(IPAddress ip) => (ip.AddressFamily == AddressFamily.InterNetwork ||
        (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId == 0 && GlobalV6.Contains(ip))) &&
        !Reserved.Any(network => network.BaseAddress.AddressFamily == ip.AddressFamily && network.Contains(ip));

    internal static async Task<IPAddress[]> ResolveAsync(string host,
        Func<string, CancellationToken, Task<IPAddress[]>> resolver, CancellationToken token)
    {
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] :
            await resolver(host.TrimEnd('.') + ".", token).ConfigureAwait(false);
        if (addresses.Length == 0 || !addresses.All(IsPublic))
            throw new HttpRequestException("Preview destination is not public.");
        return addresses;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var destination = request.RequestUri;
        if (request.Method != HttpMethod.Get || destination is null || !Allows(destination))
            throw new HttpRequestException("Preview destination is not allowed.");
        for (var hops = 0; ; hops++)
        {
            using var forwarded = new HttpRequestMessage(HttpMethod.Get, destination);
            foreach (var value in request.Headers.UserAgent) forwarded.Headers.UserAgent.Add(value);
            foreach (var value in request.Headers.Accept) forwarded.Headers.Accept.Add(value);
            foreach (var value in request.Headers.AcceptLanguage) forwarded.Headers.AcceptLanguage.Add(value);
            var response = await base.SendAsync(forwarded, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (hops >= 3 || location is null || !Uri.TryCreate(destination, location, out var next) || !Allows(next) ||
                (destination.Scheme == "https" && next.Scheme != "https")) throw new HttpRequestException("Preview redirect is not allowed.");
            destination = next;
        }
    }
}