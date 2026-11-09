using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TalkLens.Core;

namespace TalkLens.Web;

/// <summary>Works out which address phones should use, with an operator override for laptops on two networks.</summary>
public sealed class LanInfo(AppOptions options)
{
    private static readonly string[] VirtualAdapters = ["virtual", "vethernet", "wsl", "docker", "vmware", "hyper-v", "tailscale", "loopback", "bluetooth"];
    private static string HostFile => Path.Combine(Paths.Data, "public-host.txt");

    public string HttpUrl => $"http://{Host}:{options.Network.HttpPort}";
    public string HttpsUrl => $"https://{Host}:{options.Network.HttpsPort}";

    public string Host
    {
        get
        {
            if (options.Network.PublicHost.Length > 0) return options.Network.PublicHost;
            if (File.Exists(HostFile) && File.ReadAllText(HostFile).Trim() is { Length: > 0 } saved && Addresses().Contains(saved)) return saved;
            return Addresses().FirstOrDefault() ?? "localhost";
        }
    }

    public void SetHost(string host)
    {
        Directory.CreateDirectory(Paths.Data);
        File.WriteAllText(HostFile, host);
    }

    /// <summary>Private IPv4 addresses, those with a gateway first.</summary>
    public static IReadOnlyList<string> Addresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Where(n => !VirtualAdapters.Any(v => n.Description.Contains(v, StringComparison.OrdinalIgnoreCase) || n.Name.Contains(v, StringComparison.OrdinalIgnoreCase)))
            .Select(n => n.GetIPProperties())
            .OrderByDescending(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .SelectMany(p => p.UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a.Address))
            .Select(a => a.Address.ToString())
            .Distinct()
            .ToList();
    }

    public bool IsLocalHostName(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host == Host || Addresses().Contains(host);

    private static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }
}

/// <summary>Self-signed certificate for the laptop: browsers only allow camera capture over HTTPS.</summary>
public static class LocalCertificate
{
    public static X509Certificate2 LoadOrCreate()
    {
        Directory.CreateDirectory(Paths.Data);
        var pfx = Path.Combine(Paths.Data, "server.pfx");
        var hostsFile = Path.Combine(Paths.Data, "server.hosts");
        var hosts = string.Join(",", LanInfo.Addresses().Order());

        if (File.Exists(pfx) && File.Exists(hostsFile) && File.ReadAllText(hostsFile) == hosts)
        {
            var existing = new X509Certificate2(pfx);
            if (existing.NotAfter > DateTime.Now.AddDays(1)) return existing;
        }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=TalkLens local server", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        foreach (var address in LanInfo.Addresses()) names.AddIpAddress(IPAddress.Parse(address));
        request.CertificateExtensions.Add(names.Build());

        using var created = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(2));
        File.WriteAllBytes(pfx, created.Export(X509ContentType.Pfx));
        File.WriteAllText(hostsFile, hosts);
        return new X509Certificate2(pfx);
    }
}

/// <summary>Everything an attendee can do is open. Starting talks and publishing video needs the operator key.</summary>
public sealed class OperatorAuth
{
    private const string CookieName = "talklens_operator";
    private static string KeyFile => Path.Combine(Paths.Data, "operator.key");

    public string Key { get; } = LoadOrCreateKey();

    public bool IsOperator(HttpContext context) =>
        context.Request.Cookies.TryGetValue(CookieName, out var value) && Matches(value) && !Remote.IsRemote(context);

    public bool Matches(string? candidate) =>
        candidate != null && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(candidate), System.Text.Encoding.UTF8.GetBytes(Key));

    public void SignIn(HttpContext context) => context.Response.Cookies.Append(CookieName, Key, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        MaxAge = TimeSpan.FromDays(7),
    });

    private static string LoadOrCreateKey()
    {
        Directory.CreateDirectory(Paths.Data);
        if (File.Exists(KeyFile)) return File.ReadAllText(KeyFile).Trim();
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        File.WriteAllText(KeyFile, key);
        return key;
    }
}

public static class Ports
{
    private const int Tries = 10;

    /// <param name="avoid">Already chosen for something else.</param>
    public static int FirstFree(int wanted, int avoid = 0)
    {
        var taken = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(listener => listener.Port).ToHashSet();
        for (var port = wanted; port < wanted + Tries; port++)
            if (port != avoid && !taken.Contains(port)) return port;
        return wanted;
    }
}
