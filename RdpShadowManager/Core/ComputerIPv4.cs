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
    public static IPv4Choice Select(string? connected, string? primary, IEnumerable<string?> remote)
    {
        var endpoint = Normalize(connected);
        if (endpoint != null) return new(endpoint, "Установленное TCP-подключение mstsc");
        var main = Normalize(primary);
        if (main != null) return new(main, "Основной IPv4 удалённого ПК");
        var fallback = Addresses(remote).FirstOrDefault();
        return fallback == null ? new("", "IPv4 не определён") : new(fallback, "Основной IPv4 удалённого ПК");
    }
}
