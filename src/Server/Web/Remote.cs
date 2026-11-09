using System.Net;
using System.Net.Sockets;
using TalkLens.Core;

namespace TalkLens.Web;

/// <summary>
/// RF-13. People outside the venue reach the laptop through a tunnel the organizer runs, which looks like a local connection.
/// Forwarding headers are how it is told apart. It is off unless the organizer turns it on, and operator pages never open from outside.
/// </summary>
public static class Remote
{
    private static readonly string[] ForwardHeaders = ["X-Forwarded-For", "Forwarded", "Cf-Connecting-IP", "X-Real-IP", "X-Forwarded-Host"];
    private static readonly string[] OperatorPaths = ["/api/op", "/start", "/operator", "/publish", "/speaker", "/manage", "/organizer", "/checkin", "/reports"];

    public static bool IsRemote(HttpContext http)
    {
        if (ForwardHeaders.Any(h => http.Request.Headers.ContainsKey(h))) return true;
        return http.Connection.RemoteIpAddress is { } address && !IsLocal(address);
    }

    public static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>Tunnelled people all share the tunnel's address, so limits use the address it reports.</summary>
    public static string ClientKey(HttpContext http)
    {
        if (IsRemote(http))
            foreach (var header in new[] { "Cf-Connecting-IP", "X-Real-IP", "X-Forwarded-For" })
                if (http.Request.Headers[header].ToString().Split(',')[0].Trim() is { Length: > 0 } value) return "remote:" + value;
        return http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    public static void UseRemoteGuard(this WebApplication app)
    {
        app.Use(async (http, next) =>
        {
            if (!IsRemote(http))
            {
                await next();
                return;
            }
            var path = http.Request.Path.Value ?? "";
            var store = http.RequestServices.GetRequiredService<Store>();
            var isOperator = OperatorPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            var isApi = path.StartsWith("/api/v1", StringComparison.OrdinalIgnoreCase);
            if (isOperator || (!store.IsOn(Settings.RemoteEnabled) && !isApi))
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                await http.Response.WriteAsync("This event is not open from outside the venue.");
                return;
            }
            await next();
        });
    }
}
