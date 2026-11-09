using System.Net;

namespace RdpShadowManager;
internal sealed partial class MainForm
{
    internal void SetInfoSmokeFixture() => SetComputerDetails("TEST-REMOTE-WORKSTATION",
        "172.20.10.25\r\n172.20.10.26\r\n2001:db8::25",
        "Майкрософт Windows 11 Pro\r\n24H2, сборка 26100.1234",
        "Microsoft Office HomeBusinessRetail, версия 16.0.19127.20860, 64 бит (Click-to-Run)\r\nMicrosoft Visio Professional 2021, версия 16.0.14332.21040, 32 бит (MSI)");
    internal static async Task CheckComputerInfoStates()
    {
        using var form = new MainForm();
        form.Show();
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        form.host.Text = "remote-a";
        form.InfoDns = (_, _) => Task.FromException<IPAddress[]>(new InvalidOperationException("DNS unavailable"));
        form.InfoQuery = (_, _) => Task.FromException<(MaintenanceResult, int, string)>(new InvalidOperationException("WinRM unavailable"));
        await form.LoadComputerInfo();
        Check(form.infoWindows.Text.Contains("Не удалось определить"), "Unavailable info must be unknown");
        form.InfoQuery = (_, _) => Task.FromResult((new MaintenanceResult { Code = 0, Completed = true, Computer = "REMOTE-A", Windows = "Windows 11 Pro", WindowsVersion = "24H2, сборка 26100.1", OfficeKnown = true, Addresses = ["172.20.1.2", "172.20.1.3"] }, 0, ""));
        await form.LoadComputerInfo();
        Check(form.infoOffice.Text.Contains("Office не установлен"), "Confirmed absent Office");
        Check(form.infoAddresses.Lines.SequenceEqual(new[] { "172.20.1.2", "172.20.1.3" }), "All IP addresses in tooltip");
        form.InfoQuery = (_, _) => Task.FromResult((new MaintenanceResult { Code = 0, Completed = true, Office = ["Office 2019, 64 бит", "Visio 2021, 32 бит"] }, 0, ""));
        form.InfoDns = (_, _) => Task.FromResult(new[] { IPAddress.Parse("172.20.2.3") });
        await form.LoadComputerInfo();
        Check(form.ipCaption.Text.Contains("DNS") && form.infoOffice.Text.Contains("Visio 2021") && form.infoOffice.Text.Contains("остальные продукты"), "DNS source and partial product list");
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
        foreach (var field in new[] { form.infoComputer, form.infoAddresses, form.infoWindows, form.infoOffice })
        {
            Check(field.ReadOnly && field.ShortcutsEnabled && field.WordWrap && field.Multiline, "Info values must be read-only, selectable and wrapped");
            field.Focus(); field.SelectAll(); field.Copy();
            if (field.Text.Length > 0) Check(Clipboard.GetText() == field.Text, "Separate field Ctrl+C copy failed");
        }
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
        result.Completed = false;
        Check(MaintenanceOutcome(result, 1, "", true) == "unknown", "Lost completion response remains unconfirmed");
        form.Close();
    }
    private static bool HasVersionPicker(Control parent) =>
        parent.Controls.Cast<Control>().Any(c => c is CheckedListBox || HasVersionPicker(c));
}
