using RdpShadowManager.Core;

internal static class DiagnosticsIPv4Tests
{
    public static int Run()
    {
        int count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
        const string progress = "#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\"><Obj S=\"progress\"><Props><S N=\"StatusDescription\">Preparing modules for first use.</S></Props></Obj></Objs>";
        const string error = "#< CLIXML\r\n<Objs xmlns=\"http://schemas.microsoft.com/powershell/2004/04\"><S S=\"Error\">Access denied_x000D__x000A_</S></Objs>";
        var benign = PowerShellDiagnostics.Analyze(progress);
        Check(benign.Errors.Length == 0 && benign.ServiceMessages.Length == 1, "CLIXML progress is service output");
        var mixed = PowerShellDiagnostics.Analyze(progress + "\r\n" + error);
        Check(mixed.ServiceMessages.Length == 1 && mixed.Errors.Single().Contains("Access denied"), "Mixed CLIXML preserves service and real errors");
        var trailing = PowerShellDiagnostics.Analyze(progress + "\nAccess denied");
        Check(trailing.ServiceMessages.Length == 1 && trailing.Errors.Single() == "Access denied", "Trailing plain error does not discard parsed service stream");
        Check(!mixed.Errors[0].Contains("_x000D_"), "PowerShell escaped text decoded");
        Check(PowerShellDiagnostics.Analyze(" \r\n ").Errors.Length == 0, "Whitespace is not an error");
        Check(PowerShellDiagnostics.Analyze("WARNING: service notice\nVERBOSE: loading\nDEBUG: trace").ServiceMessages.Length == 3, "Known plain service streams");
        Check(PowerShellDiagnostics.Analyze("Preparing modules for first use.").Errors.Length == 0, "Known plain initialization notice");
        Check(PowerShellDiagnostics.Analyze("Access is denied.").Errors.Length == 1, "Unstructured errors remain errors");
        Check(PowerShellDiagnostics.Analyze("#< CLIXML\n<Objs><S S=\"Error\">failure</S>").Errors.Length == 1, "Malformed XML cannot hide errors");
        Check(PowerShellDiagnostics.Analyze("#< CLIXML\n<Objs><S S=\"unknown\">text</S></Objs>").Errors.Length == 1, "Unknown streams fail conservatively");
        Check(PowerShellDiagnostics.Analyze("#< CLIXML\n<Objs><S S=\"warning\">Registry error in warning text</S></Objs>").Errors.Length == 0, "Declared warning stream is nonfatal, not classified by keywords");
        Check(ComputerIPv4.IsEstablishedRdpEndpoint(5, 0x3d0d), "Native network-order port 3389 is recognized");
        Check(!ComputerIPv4.IsEstablishedRdpEndpoint(5, 0xbb01), "Gateway HTTPS port 443 is not the target PC RDP endpoint");
        Check(!ComputerIPv4.IsEstablishedRdpEndpoint(2, 0x3d0d), "Pending TCP connection is not established");
        var selected = ComputerIPv4.Select("172.31.1.11", "10.8.0.2", ["10.8.0.2", "172.31.1.11", "2001:db8::1"]);
        Check(selected.Address == "172.31.1.11" && selected.Source.Contains("TCP"), "Observed connection IPv4 takes priority over VPN/primary");
        Check(ComputerIPv4.Select(null, "172.31.1.11", ["10.8.0.2", "172.31.1.11"]).Address == "172.31.1.11", "Fallback uses remote primary");
        Check(ComputerIPv4.Select("2001:db8::1", "172.31.1.11", ["172.31.1.11"]).Address == "172.31.1.11", "IPv6 endpoint is never displayed");
        Check(ComputerIPv4.Select(null, null, ["2001:db8::1", "172.31.1.11", "10.8.0.2"]).Address == "172.31.1.11", "Legacy gateway-ordered fallback filters IPv6");
        Check(ComputerIPv4.Select(null, null, ["2001:db8::1"]).Address == "", "IPv6-only target remains unknown");
        Check(ComputerIPv4.Normalize("::ffff:172.31.1.11") == null && ComputerIPv4.Normalize("127.0.0.1") == null, "No IPv6-mapped or loopback display");
        Check(ComputerIPv4.Addresses(["172.31.1.11", "172.31.1.11", "2001:db8::1"]).SequenceEqual(new[] { "172.31.1.11" }), "IPv4 candidates deduplicated");
        Check(ComputerIPv4.Select("26.209.222.170", "172.31.1.11", ["172.31.1.11"]).Address == "172.31.1.11", "Unrelated/DNS-only 26 address is never displayed");
        Check(ComputerIPv4.Select(null, "26.209.222.170", ["172.31.1.11"]).Address == "172.31.1.11", "Primary must belong to confirmed remote interfaces");
        Check(ComputerIPv4.Select("26.209.222.170", "26.209.222.170", ["172.31.1.11", "26.209.222.170"], "172.31.1.11").Address == "172.31.1.11", "Server RDP socket confirms LAN target rather than virtual interface");
        Check(ComputerIPv4.Select("26.209.222.170", null, []).Address == "", "No confirmed data means unknown rather than a guessed address");
        Console.WriteLine($"PASS: {count} PowerShell stream/IPv4 checks");
        return count;
    }
}
