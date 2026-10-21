using System.Net;
using System.Net.Sockets;

namespace RdpShadowManager.Core;

public enum PowerAction { Restart, Shutdown, Abort }
public sealed record PowerTarget
{
    public string Computer { get; }
    public string Address { get; }
    public string MachineName => "\\\\" + (IPAddress.Parse(Address).AddressFamily == AddressFamily.InterNetworkV6
        ? Address.Replace(':', '-') + ".ipv6-literal.net" : Address);
    private PowerTarget(string computer, string address) { Computer = computer; Address = address; }
    public static PowerTarget Create(string computer, IEnumerable<IPAddress> resolved, IEnumerable<IPAddress> local, IEnumerable<string> localNames)
    {
        computer = Connection.ValidateHost(computer);
        if (computer.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            localNames.Any(n => n.TrimEnd('.').Equals(computer.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Питание локального компьютера запрещено. Укажите удалённый компьютер.");
        var addresses = resolved.Select(Normalize).Distinct().ToArray();
        var localAddresses = local.Select(Normalize).ToHashSet();
        if (localAddresses.Count == 0) throw new ArgumentException("Не удалось определить локальные адреса: запрос питания заблокирован.");
        if (addresses.Length == 0) throw new ArgumentException("IP-адрес цели не определён. Запрос питания не отправлен.");
        if (addresses.Any(a => IsForbidden(a) || localAddresses.Contains(a)))
            throw new ArgumentException("Адрес цели является локальным, служебным или неоднозначным. Запрос питания заблокирован.");
        var address = addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ThenBy(a => a.ToString(), StringComparer.Ordinal).First();
        return new(computer, address.ToString());
    }
    private static IPAddress Normalize(IPAddress value) => value.IsIPv4MappedToIPv6 ? value.MapToIPv4() : value;
    private static bool IsForbidden(IPAddress a) => IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) ||
        a.Equals(IPAddress.Broadcast) || a.IsIPv6Multicast || (a.AddressFamily == AddressFamily.InterNetwork && a.GetAddressBytes()[0] >= 224) ||
        (a.AddressFamily == AddressFamily.InterNetworkV6 && a.ScopeId != 0);
}
public sealed record PowerRequest(PowerTarget Target, PowerAction Action, int DelaySeconds = 60, string Comment = "", bool Force = false)
{
    public const int MaxDelay = 315360000;
    public const uint Reason = 0x80040000; // Planned / application / other.
    public NativeShutdownRequest Build()
    {
        if (!Enum.IsDefined(Action)) throw new ArgumentException("Неизвестное действие питания.");
        if (Action == PowerAction.Abort) throw new ArgumentException("Отмена не является запросом выключения.");
        if (DelaySeconds < 0 || DelaySeconds > MaxDelay) throw new ArgumentException("Задержка вне допустимого диапазона.");
        if (Comment == null || Comment.Length > 512 || Comment.Contains('\0')) throw new ArgumentException("Комментарий должен содержать не более 512 символов и не содержать NUL.");
        return new(Target.MachineName, Comment, (uint)DelaySeconds, Force, Action == PowerAction.Restart, Reason);
    }
}
public sealed record NativeShutdownRequest(string MachineName, string Comment, uint DelaySeconds, bool ForceAppsClosed, bool Reboot, uint Reason);
public interface IPowerNative
{
    int Initiate(NativeShutdownRequest request);
    int Abort(string machineName);
}
public static class PowerOperations
{
    // Caller supplies a freshly revalidated, immutable target. Tests inject a recording adapter; never call Windows power APIs.
    public static int Execute(PowerRequest request, IPowerNative native)
    {
        if (!Enum.IsDefined(request.Action)) throw new ArgumentException("Неизвестное действие питания.");
        if (request.Target == null || !request.Target.MachineName.StartsWith("\\\\", StringComparison.Ordinal) || request.Target.MachineName.Length <= 2)
            throw new ArgumentException("Удалённая цель не указана.");
        return request.Action == PowerAction.Abort ? native.Abort(request.Target.MachineName) : native.Initiate(request.Build());
    }
    public static string ErrorMessage(int code) => code switch
    {
        5 or 1314 => "Доступ запрещён или недостаточно прав. Требуется право принудительного завершения работы из удалённой системы на целевом компьютере.",
        53 or 64 or 67 or 1231 or 1722 => "Удалённый компьютер или служба RPC недоступны. Проверьте адрес, сеть и разрешённые правила брандмауэра.",
        1115 or 1190 => "На целевом компьютере уже запланировано или выполняется завершение работы. Запрос не заменён.",
        1116 => "На целевом компьютере нет запланированного действия для отмены либо отменять его уже поздно.",
        21 => "Удалённый компьютер пока не готов принять запрос.",
        _ => "Windows отклонила запрос питания. См. системный код и диагностику."
    };
    public static string Accepted(PowerRequest request) => request.Action switch
    {
        PowerAction.Restart => $"Запрос на перезагрузку принят. Цель: {request.Target.Computer} ({request.Target.Address}); задержка {request.DelaySeconds} с.",
        PowerAction.Shutdown => $"Запрос на выключение принят. Цель: {request.Target.Computer} ({request.Target.Address}); задержка {request.DelaySeconds} с.",
        PowerAction.Abort => $"Запрос на отмену запланированного действия принят. Цель: {request.Target.Computer} ({request.Target.Address}).",
        _ => throw new ArgumentException("Неизвестное действие питания.")
    };
}
