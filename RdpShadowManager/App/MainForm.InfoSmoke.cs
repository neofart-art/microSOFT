using System.Net;

namespace RdpShadowManager;
internal sealed partial class MainForm
{
    internal void SetInfoSmokeFixture() { SetComputerDetails("TEST-REMOTE-WORKSTATION",
        "IPv4: 172.20.10.25",
        "Майкрософт Windows 11 Pro\r\n24H2, сборка 26100.1234",
        "Microsoft Office HomeBusinessRetail, версия 16.0.19127.20860, 64 бит (Click-to-Run)\r\nMicrosoft Visio Professional 2021, версия 16.0.14332.21040, 32 бит (MSI)");
        ShowHardwareSummary(new SystemInfoData { Model = "TEST Laptop", SerialNumber = "TEST-SERIAL", LastBoot = "2026-11-08T10:00:00Z",
            UptimeSeconds = 86400, InstalledRamBytes = 17179869184, Cpus = [new() { Name = "TEST CPU" }] });
    }
    internal static async Task CheckComputerInfoStates()
    {
        using var form = new MainForm();
        form.Show();
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        form.InfoConnectedIPv4 = (_, _, _) => Task.FromResult<string?>(null);
        form.host.Text = "remote-a";
        form.InfoDns = (_, _) => Task.FromException<IPAddress[]>(new InvalidOperationException("DNS unavailable"));
        form.InfoQuery = (_, _) => Task.FromException<(MaintenanceResult, int, string)>(new InvalidOperationException("WinRM unavailable"));
        await form.LoadComputerInfo();
        Check(form.infoWindows.Text.Contains("Не удалось определить"), "Unavailable info must be unknown");
        form.InfoQuery = (_, _) => Task.FromResult((new MaintenanceResult { Code = 0, Completed = true, Computer = "REMOTE-A", Windows = "Windows 11 Pro", WindowsVersion = "24H2, сборка 26100.1", OfficeKnown = true, PrimaryIPv4 = "172.20.1.3", Addresses = ["2001:db8::1", "172.20.1.2", "172.20.1.3"] }, 0, ""));
        await form.LoadComputerInfo();
        Check(form.infoOffice.Text.Contains("Office не установлен"), "Confirmed absent Office");
        Check(form.infoAddresses.Text == "IPv4: 172.20.1.3", "Only primary IPv4 without IPv6 is displayed");
        form.InfoQuery = (_, _) => Task.FromResult((new MaintenanceResult { Code = 0, Completed = true, PrimaryIPv4 = "10.8.0.2", RdpIPv4 = "172.31.1.11", Addresses = ["10.8.0.2", "172.31.1.11"], OfficeKnown = true }, 0, ""));
        form.InfoConnectedIPv4 = (_, _, _) => Task.FromResult<string?>("26.209.222.170");
        await form.LoadComputerInfo();
        Check(form.infoAddresses.Text == "IPv4: 172.31.1.11" && form.infoAddresses.Lines.Length == 1, "Remote RDP socket wins over unrelated 26 address and VPN primary");
        form.InfoConnectedIPv4 = (_, _, _) => Task.FromResult<string?>(null);
        form.InfoQuery = (_, _) => Task.FromResult((new MaintenanceResult { Code = 0, Completed = true, Office = ["Office 2019, 64 бит", "Visio 2021, 32 бит"] }, 0, ""));
        form.InfoDns = (_, _) => Task.FromResult(new[] { IPAddress.Parse("172.20.2.3") });
        await form.LoadComputerInfo();
        Check(form.infoAddresses.Text == "IPv4: Не удалось определить" && form.infoOffice.Text.Contains("Visio 2021") && form.infoOffice.Text.Contains("остальные продукты"), "DNS guesses are not presented as observed connection/remote primary");
        var pending = new TaskCompletionSource<(MaintenanceResult, int, string)>();
        form.InfoQuery = (_, _) => pending.Task;
        var old = form.LoadComputerInfo();
        Check(form.infoAddresses.Text == "Получение сведений…" && !form.infoRefresh.Enabled, "Loading state");
        form.host.Text = "remote-b";
        Check(!form.infoComputer.Text.Contains("REMOTE-A") && !form.infoRefresh.Enabled, "Host change clears info immediately");
        form.InfoQuery = (_, _) => Task.FromResult((new MaintenanceResult { Code = 0, Completed = true, Computer = "REMOTE-B", OfficeKnown = true }, 0, ""));
        await form.LoadComputerInfo();
        var expected = form.infoComputer.Text;
        pending.SetResult((new MaintenanceResult { Code = 0, Completed = true, Computer = "STALE-A" }, 0, ""));
        await old;
        Check(form.infoComputer.Text == expected && form.infoRefresh.Enabled, "Late response must not replace selected host");
        form.ShowServiceNotice("Настройки применены", Color.ForestGreen, "remote-b", "DOMAIN\\user");
        Check(form.serviceNotice.Text == "Настройки применены" && form.serviceNotice.ForeColor == Color.ForestGreen, "Persistent maintenance result metadata");
        foreach (var field in new[] { form.infoComputer, form.infoAddresses, form.infoWindows, form.infoOffice }.Concat(form.hardwareFields.Values))
        {
            Check(field.ReadOnly && field.ShortcutsEnabled && field.WordWrap && field.Multiline, "Info values must be read-only, selectable and wrapped");
            field.Focus(); field.SelectAll(); field.Copy();
            if (field.Text.Length > 0) Check(Clipboard.GetText() == field.Text, "Separate field Ctrl+C copy failed");
        }
        form.ShowHardwareSummary(new SystemInfoData { Model = "TEST Laptop", SerialNumber = "TEST-SERIAL", LastBoot = "2026-11-08T10:00:00Z",
            UptimeSeconds = 86400, InstalledRamBytes = 17179869184, Cpus = [new() { Name = "TEST CPU" }] });
        Check(form.hardwareFields["model"].Text == "TEST Laptop" && form.hardwareFields["cpu"].Text == "TEST CPU"
            && form.hardwareFields["uptime"].Text == "1 д 00:00:00", "Sidebar summary shows requested hardware and uptime");
        form.host.Text = "new-pc";
        Check(form.hardwareFields.Values.All(f => f.Text.Contains("Не удалось")), "Host change clears old hardware summary");
        Clipboard.Clear();
        var start = MaintenanceClient.HiddenPowerShellStart("powershell.exe");
        Check(start.CreateNoWindow && !start.UseShellExecute && start.WindowStyle == System.Diagnostics.ProcessWindowStyle.Hidden
            && start.RedirectStandardError && start.RedirectStandardOutput, "Maintenance process must be hidden and redirected");
        using (var configuration = new MaintenanceDialog("remote-b", new MaintenanceResult { Versions = ["16.0"] }, _ => Task.FromResult(new MaintenanceResult())))
            Check(!HasVersionPicker(configuration), "Manual Outlook version picker must not exist");
        form.host.Text = "remote-c";
        Check(!form.serviceNotice.Visible, "Host change clears maintenance result");
        var result = new MaintenanceResult { Code = 0 };
        Check(MaintenanceOutcome(result, 0, "", true) == "unknown", "Starting a script is not success");
        result.Completed = true;
        Check(MaintenanceOutcome(result, 0, "", true) == "error", "Missing value verification is not success");
        result.Verified = true;
        Check(MaintenanceOutcome(result, 0, "", true) == "success", "Verified successful completion");
        Check(MaintenanceOutcome(result, 1, "", true) == "error", "Nonzero exit is not success");
        Check(MaintenanceOutcome(result, 0, "error", true) == "error", "Transport stderr prevents success");
        Check(MaintenanceOutcome(result, 0, "", false) == "error", "Mismatched identity prevents success");
        result.Success = 1; result.Failed = 1; result.Errors = ["write failed"];
        Check(MaintenanceOutcome(result, 1, "", true) == "partial", "Partial writes must be orange");
        var partialSummary = MaintenanceSummary(result, "partial", false, "remote-c", "DOMAIN\\user", "");
        Check(partialSummary.Contains("write failed") && partialSummary.Contains("Почему не полностью"), "Partial result must explain its reason");
        Check(MaintenanceTitle("success", false) == "Настройки применены" && MaintenanceTitle("partial", false) == "Применены частично"
            && MaintenanceTitle("error", false) == "Не удалось применить" && MaintenanceTitle("unknown", false) == "Результат не подтверждён", "Short final status wording");
        var unchanged = new MaintenanceResult { Completed = true, Verified = true, Code = 0, Success = 0, Skipped = 36, Failed = 0 };
        const string initialization = "#< CLIXML\r\n<Objs xmlns=\"http://schemas.microsoft.com/powershell/2004/04\"><Obj S=\"progress\"><S N=\"StatusDescription\">Preparing modules for first use.</S></Obj></Objs>";
        var already = MaintenanceOutcome(unchanged, 0, initialization, true);
        Check(already == "current", "0 changed, 36 matching, no errors, exit 0 and service stderr is already current");
        Check(MaintenanceTitle(already, false) == "Настройки AutoDiscover уже актуальны. Изменения не требуются"
            && !ShowMaintenanceResultDialog(already), "Already current uses exact label without warning/modal");
        form.ShowServiceNotice(MaintenanceTitle(already, false), Color.ForestGreen, "remote-c", "DOMAIN\\user");
        Check(form.serviceNotice.ForeColor == Color.ForestGreen && form.serviceNotice.Text == MaintenanceTitle(already, false), "Already current is a green result");
        Check(MaintenanceOutcome(unchanged, 0, initialization + "\nAccess denied", true) == "error", "Mixed or invalid error output still blocks success");
        Check(MaintenanceOutcome(unchanged, 0, "#< CLIXML\n<Objs><S S=\"Error\">Access denied</S></Objs>", true) == "error", "Real error stream blocks current result");
        unchanged.Completed = false;
        Check(MaintenanceOutcome(unchanged, 0, initialization, true) == "unknown", "Service output cannot confirm an unfinished operation");
        result.Completed = false;
        Check(MaintenanceOutcome(result, 1, "", true) == "unknown", "Lost completion response remains unconfirmed");
        form.Close();
    }
    internal static void CheckHiddenPowerShell()
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        var start = MaintenanceClient.HiddenPowerShellStart(executable);
        const string probe = """
            $ErrorActionPreference='Stop'
            Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class RsmConsoleProbe { [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow(); }'
            [Console]::Write([RsmConsoleProbe]::GetConsoleWindow().ToInt64())
            """;
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", probe }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("PowerShell console probe did not start");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000))
        {
            process.Kill(true); process.WaitForExit();
            throw new InvalidOperationException("PowerShell console probe timed out");
        }
        var actual = output.GetAwaiter().GetResult().Trim();
        var stderr = error.GetAwaiter().GetResult();
        if (process.ExitCode != 0 || actual != "0")
            throw new InvalidOperationException($"PowerShell has a console or probe failed: exit={process.ExitCode}, handle={actual}, {stderr}");
    }
    private static bool HasVersionPicker(Control parent) =>
        parent.Controls.Cast<Control>().Any(c => c is CheckedListBox || HasVersionPicker(c));
}
