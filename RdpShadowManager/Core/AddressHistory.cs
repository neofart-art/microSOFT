using System.Text.Json;

namespace RdpShadowManager.Core;

/// <summary>Only validated computer names/IPs, most recent first. No credentials or session details.</summary>
public sealed class AddressHistoryStore
{
    public const int Capacity = 30;
    public const int MaxFileBytes = 65536;
    public string FilePath { get; }
    public AddressHistoryStore(string filePath) => FilePath = Path.GetFullPath(filePath);
    public static string[] Normalize(IEnumerable<string?> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var entry in entries)
        {
            if (entry == null) continue;
            string address;
            try { address = Connection.ValidateHost(entry); }
            catch (ArgumentException) { continue; }
            if (seen.Add(address)) result.Add(address);
            if (result.Count == Capacity) break;
        }
        return result.ToArray();
    }
    public string[] Load()
    {
        if (!File.Exists(FilePath)) return [];
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxFileBytes) throw new InvalidDataException("Файл истории адресов слишком велик.");
        return Normalize(JsonSerializer.Deserialize<string?[]>(stream) ?? []);
    }
    public void Save(IEnumerable<string?> entries)
    {
        var addresses = Normalize(entries);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, addresses);
                stream.Flush(true);
            }
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
