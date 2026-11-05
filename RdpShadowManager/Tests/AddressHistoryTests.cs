using RdpShadowManager.Core;

internal static class AddressHistoryTests
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); passed++; }
        var folder = Path.Combine(Path.GetTempPath(), "RdpShadowManager-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(folder, "history.json");
            var store = new AddressHistoryStore(path);
            Check(store.Load().Length == 0, "Missing history starts empty");
            store.Save([" PC-01 ", "pc-01", "", "PC /admin", null, "192.0.2.10", "2001:db8::1"]);
            var loaded = new AddressHistoryStore(path).Load();
            Check(loaded.SequenceEqual(new[] { "PC-01", "192.0.2.10", "2001:db8::1" }), "Persistence, trim, validation and case-insensitive duplicates");
            Check(AddressHistoryStore.Normalize(["pc-01", .. loaded]).SequenceEqual(new[] { "pc-01", "192.0.2.10", "2001:db8::1" }), "Remembered item goes first once");
            store.Save(Enumerable.Range(0, 50).Select(i => "PC-" + i));
            Check(store.Load().Length == AddressHistoryStore.Capacity, "Bounded history");
            Check(store.Load().First() == "PC-0" && store.Load().Last() == "PC-29", "MRU order preserved when trimming");
            Check(Directory.GetFiles(folder, "*.tmp").Length == 0, "Atomic save leaves no temporary file");
            store.Save(["LAST-PC"]);
            Check(store.Load().SequenceEqual(new[] { "LAST-PC" }), "Existing history atomically replaced");
            File.WriteAllText(path, "{broken");
            try { store.Load(); throw new Exception("Malformed history accepted"); }
            catch (System.Text.Json.JsonException) { passed++; }
            Check(File.ReadAllText(path) == "{broken", "Loading corrupt history does not modify it");
            File.WriteAllText(path, new string(' ', AddressHistoryStore.MaxFileBytes + 1));
            try { store.Load(); throw new Exception("Oversized history accepted"); }
            catch (InvalidDataException) { passed++; }
            var blocked = new AddressHistoryStore(folder);
            try { blocked.Save(["PC-01"]); throw new Exception("Save to directory unexpectedly succeeded"); }
            catch (IOException) { passed++; }
            Check(!Directory.GetFiles(Path.GetDirectoryName(folder)!, Path.GetFileName(folder) + ".*.tmp").Any(), "Failed save cleans temporary file");
            store.Save([]);
            Check(store.Load().Length == 0, "Empty history roundtrip");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        Console.WriteLine($"PASS: {passed} address history checks");
        return passed;
    }
}
