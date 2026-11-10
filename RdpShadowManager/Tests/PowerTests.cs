using System.Net;
using RdpShadowManager.Core;

internal static class PowerTests
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool value, string description) { if (!value) throw new Exception(description); passed++; }
        void Reject(Action action) { try { action(); } catch (ArgumentException) { passed++; return; } throw new Exception("Unsafe power input accepted"); }
        var local = new[] { IPAddress.Parse("10.0.0.1"), IPAddress.Parse("2001:db8::1") };
        var target = PowerTarget.Create("pc.example", [IPAddress.Parse("10.0.0.2")], local, ["ADMIN-PC"]);
        Check(target.MachineName == @"\\10.0.0.2", "Pinned explicit remote API name");
        var defaults = new PowerRequest(target, PowerAction.Restart);
        Check(defaults.DelaySeconds == 60 && !defaults.Force, "Safe default delay and force");
        var fake = new Fake();
        foreach (var action in new[] { PowerAction.Restart, PowerAction.Shutdown })
        foreach (var delay in new[] { 0, 60, 120 })
        foreach (var force in new[] { false, true })
        {
            var request = new PowerRequest(target, action, delay, "Сохраните документы", force);
            Check(PowerOperations.Execute(request, fake) == 0, "Native result propagated");
            Check(fake.Last == new NativeShutdownRequest(@"\\10.0.0.2", "Сохраните документы", (uint)delay, force, action == PowerAction.Restart, PowerRequest.Reason), "Native parameters exact; delay never enables force");
        }
        int starts = fake.Starts;
        Check(PowerOperations.Execute(new(target, PowerAction.Abort), fake) == 0, "Abort accepted");
        Check(fake.Starts == starts && fake.Aborts == 1 && fake.AbortTarget == @"\\10.0.0.2", "Abort only calls abort on same pinned remote");
        foreach (int error in new[] { 5, 1314, 53, 1722, 1115, 1190, 1116, 21, 9999 })
        {
            fake.Code = error;
            Check(PowerOperations.Execute(defaults, fake) == error, "Shutdown error preserved");
            Check(PowerOperations.Execute(new(target, PowerAction.Abort), fake) == error, "Abort error preserved");
            Check(PowerOperations.ErrorMessage(error).Length > 0, "Error explanation available");
        }
        foreach (var computer in new[] { "", " ", ".", @"\\PC", "PC /f", "localhost", "ADMIN-PC" })
            Reject(() => PowerTarget.Create(computer, [IPAddress.Parse("10.0.0.2")], local, ["ADMIN-PC"]));
        foreach (var address in new[] { "127.0.0.1", "127.0.0.2", "::1", "0.0.0.0", "::", "::ffff:127.0.0.1", "10.0.0.1", "2001:db8::1", "255.255.255.255", "224.0.0.1", "ff02::1", "fe80::1%12" })
            Reject(() => PowerTarget.Create("remote-alias", [IPAddress.Parse(address)], local, []));
        Reject(() => PowerTarget.Create("mixed-alias", [IPAddress.Parse("10.0.0.2"), IPAddress.Parse("10.0.0.1")], local, []));
        Reject(() => PowerTarget.Create("unresolved", [], local, []));
        Reject(() => PowerTarget.Create("remote", [IPAddress.Parse("10.0.0.2")], [], []));
        Reject(() => (defaults with { DelaySeconds = -1 }).Build());
        Reject(() => (defaults with { DelaySeconds = PowerRequest.MaxDelay + 1 }).Build());
        Reject(() => (defaults with { Action = (PowerAction)99 }).Build());
        Reject(() => PowerOperations.Execute(defaults with { Action = (PowerAction)99 }, fake));
        Reject(() => (defaults with { Comment = "bad\0comment" }).Build());
        Reject(() => (defaults with { Comment = new string('x', 513) }).Build());
        Check((defaults with { Comment = new string('x', 512) }).Build().Comment.Length == 512, "Maximum comment preserved");
        var v6 = PowerTarget.Create("v6remote", [IPAddress.Parse("2001:db8::2")], local, []);
        Check(v6.MachineName == @"\\2001-db8--2.ipv6-literal.net", "IPv6 UNC literal");
        var pinned = defaults.Target;
        string editable = "other-pc";
        Check(defaults.Target == pinned && defaults.Target.Computer != editable, "Dialog request keeps immutable target");
        Check(PowerOperations.Accepted(defaults).Contains("Запрос на перезагрузку принят"), "No unverified completion claim");
        Check(PowerOperations.Accepted(defaults with { Action = PowerAction.Shutdown }).Contains("Запрос на выключение принят"), "Shutdown acceptance wording");
        Console.WriteLine($"PASS: {passed} power checks (fake native API only; no real shutdown or abort)");
        return passed;
    }
    private sealed class Fake : IPowerNative
    {
        public int Code, Starts, Aborts;
        public NativeShutdownRequest? Last;
        public string? AbortTarget;
        public int Initiate(NativeShutdownRequest request) { Starts++; Last = request; return Code; }
        public int Abort(string machineName) { Aborts++; AbortTarget = machineName; return Code; }
    }
}
