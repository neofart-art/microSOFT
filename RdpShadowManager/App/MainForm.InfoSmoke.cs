using System.Net;

namespace RdpShadowManager;
internal sealed partial class MainForm
{
    internal static async Task CheckComputerInfoStates()
    {
        using var form = new MainForm();
        form.Show();
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        form.host.Text = "remote-a";
        form.InfoDns = (_, _) => Task.FromException<IPAddress[]>(new InvalidOperationException("DNS unavailable"));
        form.InfoQuery = (_, _) => Task.FromException<(MaintenanceResult, int, string)>(new InvalidOperationException("WinRM unavailable"));
        await form.LoadComputerInfo();
        Check(form.computerInfo.Text.Contains("Не удалось определить"), "Unavailable info must be unknown");
        form.InfoQuery = (_, _) => Task.FromResult((new MaintenanceResult { Code = 0, Completed = true, Computer = "REMOTE-A", Windows = "Windows 11 Pro", WindowsVersion = "24H2, сборка 26100.1", OfficeKnown = true, Addresses = ["172.20.1.2", "172.20.1.3"] }, 0, ""));
        await form.LoadComputerInfo();
        Check(form.computerInfo.Text.Contains("Office не установлен"), "Confirmed absent Office");
        Check((form.infoTips.GetToolTip(form.computerInfo) ?? "").Contains("172.20.1.3"), "All IP addresses in tooltip");
        form.InfoQuery = (_, _) => Task.FromResult((new MaintenanceResult { Code = 0, Completed = true, Office = ["Office 2019, 64 бит", "Visio 2021, 32 бит"] }, 0, ""));
        form.InfoDns = (_, _) => Task.FromResult(new[] { IPAddress.Parse("172.20.2.3") });
        await form.LoadComputerInfo();
        Check(form.computerInfo.Text.Contains("IP по DNS") && form.computerInfo.Text.Contains("Visio 2021") && form.computerInfo.Text.Contains("остальные продукты"), "DNS source and partial product list");
        var pending = new TaskCompletionSource<(MaintenanceResult, int, string)>();
        form.InfoQuery = (_, _) => pending.Task;
        var old = form.LoadComputerInfo();
        Check(form.computerInfo.Text == "Получение сведений…" && !form.infoRefresh.Enabled, "Loading state");
        form.host.Text = "remote-b";
        Check(!form.computerInfo.Text.Contains("REMOTE-A") && !form.infoRefresh.Enabled, "Host change clears info immediately");
        form.InfoQuery = (_, _) => Task.FromResult((new MaintenanceResult { Code = 0, Completed = true, Computer = "REMOTE-B", OfficeKnown = true }, 0, ""));
        await form.LoadComputerInfo();
        var expected = form.computerInfo.Text;
        pending.SetResult((new MaintenanceResult { Code = 0, Completed = true, Computer = "STALE-A" }, 0, ""));
        await old;
        Check(form.computerInfo.Text == expected && form.infoRefresh.Enabled, "Late response must not replace selected host");
        form.ShowServiceNotice("Настройки Outlook AutoDiscover успешно применены", Color.ForestGreen, "remote-b", "DOMAIN\\user");
        Check(form.serviceNotice.Text.Contains("Завершено:") && form.serviceNotice.ForeColor == Color.ForestGreen, "Persistent maintenance result metadata");
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
        result.Completed = false;
        Check(MaintenanceOutcome(result, 1, "", true) == "unknown", "Lost completion response remains unconfirmed");
        form.Close();
    }
}
