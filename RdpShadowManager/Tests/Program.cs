using RdpShadowManager.Core;
int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }
void Reject(Action action) { try { action(); } catch (ArgumentException) { passed++; return; } throw new Exception("Invalid input accepted"); }
foreach (var name in new[] { "PC-01", "pc.example.org", "192.168.1.10", "2001:db8::1" }) Check(Connection.ValidateHost(name) == name, name);
Check(Connection.ValidateHost(" PC ") == "PC", "trim");
foreach (var invalid in new[] { "", " ", "PC /admin", "PC\t/control", "/v:PC", "-PC", "PC;calc", "PC\\share", "a..b", "PC\"", "[::1]" })
{
    Reject(() => Connection.ValidateHost(invalid));
}
Check(Connection.Arguments("PC", 12, false).SequenceEqual(new[] { "/v:PC", "/shadow:12", "/control" }), "consent arguments");
Check(Connection.Arguments("192.168.1.10", 12, true).SequenceEqual(new[] { "/v:192.168.1.10", "/shadow:12", "/control", "/noconsentprompt" }), "no consent arguments");
Reject(() => Connection.Arguments("PC", 0, false)); Reject(() => Connection.Arguments("PC", -1, true));
Reject(() => Connection.Arguments("PC /admin", 1, false));
var active = new Session(12, "Alice", "DOMAIN", "rdp-tcp#1", 0);
Check(Connection.SameActiveUser(active, active with { User = "alice" }), "same user");
Check(!Connection.SameActiveUser(active, active with { User = "Bob" }), "reused ID");
Check(!Connection.SameActiveUser(active, active with { State = 4 }), "disconnected");
Check(!Connection.SameActiveUser(active, active with { Id = 13 }), "different ID");
Check(!Connection.SameActiveUser(active, active with { Domain = "OTHER" }), "different domain");
Check(!(active with { Id = 0 }).CanConnect, "session zero");
Check(!(active with { User = "" }).CanConnect, "service session");
Console.WriteLine($"PASS: {passed} checks");
