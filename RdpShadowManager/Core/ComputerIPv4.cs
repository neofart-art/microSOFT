using System.Net;
using System.Net.Sockets;

namespace RdpShadowManager.Core;
public sealed record IPv4Choice(string Address, string Source);
public static class ComputerIPv4
{
    public static string? Normalize(string? text) => IPAddress.TryParse(text, out var ip)
        && ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip)
        && !ip.Equals(IPAddress.Any) && ip.GetAddressBytes()[0] < 224 ? ip.ToString() : null;
    public static bool IsEstablishedRdpEndpoint(int state, int networkOrderPort) =>
        state == 5 && (ushort)IPAddress.NetworkToHostOrder((short)networkOrderPort) == 3389;
    public static string[] Addresses(IEnumerable<string?> values) =>
        values.Select(Normalize).Where(a => a != null).Cast<string>().Distinct().ToArray();
    public static IPv4Choice Select(string? connected, string? primary, IEnumerable<string?> remote, string? serverConnected = null)
    {
        var known = Addresses(remote);
        var endpoint = Normalize(connected);
        var server = Normalize(serverConnected);
        if (server != null && known.Contains(server))
            return new(server, "RDP-соединение подтверждено на удалённом ПК");
        if (endpoint != null && known.Contains(endpoint))
            return new(endpoint, "Подтверждённое TCP-подключение mstsc");
        var main = Normalize(primary);
        if (main != null && known.Contains(main)) return new(main, "Основной IPv4 удалённого ПК");
        return new("", "Адрес соединения или основной LAN IPv4 не подтверждён");
    }
}
