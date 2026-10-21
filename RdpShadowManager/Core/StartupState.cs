using System.Text;
using System.Text.Json;

namespace RdpShadowManager.Core;

// No session data crosses the UAC boundary: it must be fetched again.
public sealed record StartupState(string Computer, bool NoConsent)
{
    public string Encode() => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this)));
    public static StartupState Decode(string encoded)
    {
        if (encoded.Length > 16384) throw new ArgumentException("Слишком длинное состояние перезапуска.");
        try
        {
            var state = JsonSerializer.Deserialize<StartupState>(Convert.FromBase64String(encoded));
            if (state == null || state.Computer == null || state.Computer.Length > 4096)
                throw new ArgumentException("Некорректное состояние перезапуска.");
            return state;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        { throw new ArgumentException("Некорректное состояние перезапуска.", ex); }
    }
}
