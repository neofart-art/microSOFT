using System.Net;
using System.Text.RegularExpressions;

namespace RdpShadowManager.Core;

public sealed record Session(int Id, string User, string Domain, string Name, int State)
{
    public bool CanConnect => Id > 0 && User.Length > 0 && State == 0;
    public string Status => State switch { 0 => "Активен", 1 => "Подключён", 2 => "Запрос подключения", 3 => "Теневой", 4 => "Отключён", 5 => "Ожидание", 6 => "Прослушивание", 7 => "Сброс", 8 => "Недоступен", 9 => "Инициализация", _ => $"Неизвестно ({State})" };
}
public sealed record QueryResult(List<Session> Sessions, List<string> Diagnostics, int ErrorCode = 0, string? Error = null);
public static class Connection
{
    public static string ValidateHost(string value)
    {
        var host = value.Trim();
        if (host.Length is 0 or > 253 || host.Any(char.IsWhiteSpace) ||
            !(IPAddress.TryParse(host, out _) || Regex.IsMatch(host, @"\A[a-zA-Z0-9](?:[a-zA-Z0-9.-]*[a-zA-Z0-9])?\z")) ||
            host.Contains(".."))
            throw new ArgumentException("Введите имя компьютера или IP без пробелов, путей и ключей командной строки.");
        return host;
    }
    public static string[] Arguments(string host, int id, bool noConsent)
    {
        host = ValidateHost(host);
        if (id <= 0) throw new ArgumentException("ID сеанса должен быть положительным.");
        return noConsent ? [$"/v:{host}", $"/shadow:{id}", "/control", "/noconsentprompt"] : [$"/v:{host}", $"/shadow:{id}", "/control"];
    }
    public static bool SameActiveUser(Session before, Session after) => after.CanConnect && before.Id == after.Id &&
        string.Equals(before.User, after.User, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(before.Domain, after.Domain, StringComparison.OrdinalIgnoreCase);
}
