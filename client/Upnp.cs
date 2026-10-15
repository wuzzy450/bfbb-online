using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace BfbbOnline.Client;

/// <summary>
/// Opens the server's TCP port on the home router with UPnP (IGD), so friends on the internet can
/// connect to a PC behind NAT, and closes it again when hosting stops. Best effort: many routers
/// have UPnP turned off, then the port has to be forwarded by hand.
/// </summary>
public sealed class Upnp : IDisposable
{
    readonly string controlUrl, serviceType;
    readonly int port;
    bool mapped;

    public string? ExternalIp { get; private set; }

    Upnp(string controlUrl, string serviceType, int port)
    {
        this.controlUrl = controlUrl;
        this.serviceType = serviceType;
        this.port = port;
    }

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    /// <summary>Finds the router and maps <paramref name="port"/> to this PC. Null (and a reason) if it can't.</summary>
    public static async Task<(Upnp? map, string message)> OpenAsync(int port)
    {
        try
        {
            var (location, st) = await DiscoverAsync();
            if (location == null) return (null, "no UPnP router answered (UPnP may be off: forward the port by hand)");
            var desc = XDocument.Parse(await Http.GetStringAsync(location));
            XNamespace ns = desc.Root!.Name.Namespace;
            var svc = desc.Descendants(ns + "service").FirstOrDefault(s =>
                (string?)s.Element(ns + "serviceType") is string t && (t.Contains("WANIPConnection") || t.Contains("WANPPPConnection")));
            if (svc == null) return (null, "the router has no WAN IP connection service");
            var ctl = new Uri(new Uri(location), (string)svc.Element(ns + "controlURL")!).ToString();
            var map = new Upnp(ctl, (string)svc.Element(ns + "serviceType")!, port);
            string local = LocalAddressFor(new Uri(location).Host) ?? "";
            if (local.Length == 0) return (null, "couldn't tell this PC's LAN address");
            await map.SoapAsync("AddPortMapping",
                $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{port}</NewExternalPort><NewProtocol>TCP</NewProtocol>" +
                $"<NewInternalPort>{port}</NewInternalPort><NewInternalClient>{local}</NewInternalClient><NewEnabled>1</NewEnabled>" +
                "<NewPortMappingDescription>BFBB Online</NewPortMappingDescription><NewLeaseDuration>0</NewLeaseDuration>");
            map.mapped = true;
            try
            {
                var r = await map.SoapAsync("GetExternalIPAddress", "");
                map.ExternalIp = Regex.Match(r, "<NewExternalIPAddress>([^<]*)</NewExternalIPAddress>").Groups[1].Value;
            }
            catch (Exception) { }
            return (map, $"router port {port} opened (UPnP) for {local}" + (map.ExternalIp is { Length: > 0 } ip ? $"; friends connect to {ip}:{port}" : ""));
        }
        catch (Exception e)
        {
            return (null, $"UPnP failed: {e.Message} (forward TCP port {port} to this PC by hand)");
        }
    }

    static async Task<(string? location, string? st)> DiscoverAsync()
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        var target = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        foreach (var st in new[] { "urn:schemas-upnp-org:device:InternetGatewayDevice:1", "urn:schemas-upnp-org:service:WANIPConnection:1", "upnp:rootdevice" })
        {
            var req = Encoding.ASCII.GetBytes(
                "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: " + st + "\r\n\r\n");
            await udp.SendAsync(req, req.Length, target);
            var until = DateTime.UtcNow.AddSeconds(2.5);
            while (DateTime.UtcNow < until)
            {
                var recv = udp.ReceiveAsync();
                if (await Task.WhenAny(recv, Task.Delay(until - DateTime.UtcNow)) != recv) break;
                var text = Encoding.ASCII.GetString(recv.Result.Buffer);
                var m = Regex.Match(text, @"^LOCATION:\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                if (!m.Success) continue;
                // a root device answer still has to describe a gateway: checked by the caller
                if (st == "upnp:rootdevice")
                {
                    try { if (!(await Http.GetStringAsync(m.Groups[1].Value)).Contains("WANIPConnection") ) continue; }
                    catch (Exception) { continue; }
                }
                return (m.Groups[1].Value, st);
            }
        }
        return (null, null);
    }

    static string? LocalAddressFor(string routerHost)
    {
        // the local address on the router's subnet: open a UDP "connection" and read our end
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(routerHost, 1900);
            return ((IPEndPoint)s.LocalEndPoint!).Address.ToString();
        }
        catch (Exception)
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();
        }
    }

    async Task<string> SoapAsync(string action, string args)
    {
        string body = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
            "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
            $"<u:{action} xmlns:u=\"{serviceType}\">{args}</u:{action}></s:Body></s:Envelope>";
        using var req = new HttpRequestMessage(HttpMethod.Post, controlUrl) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };
        req.Headers.TryAddWithoutValidation("SOAPAction", $"\"{serviceType}#{action}\"");
        using var resp = await Http.SendAsync(req);
        string text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"{action}: HTTP {(int)resp.StatusCode}");
        return text;
    }

    /// <summary>Removes the mapping again.</summary>
    public void Dispose()
    {
        if (!mapped) return;
        mapped = false;
        try
        {
            SoapAsync("DeletePortMapping", $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{port}</NewExternalPort><NewProtocol>TCP</NewProtocol>")
                .Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception) { }
    }
}
