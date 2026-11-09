using System.Drawing.Imaging;

namespace RdpShadowManager;
internal static class SystemInfoSmoke
{
    public static async Task Run(string output)
    {
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        var data = new SystemInfoData
        {
            CapturedAt = "2026-11-09T10:00:00Z", Manufacturer = "Test manufacturer", Model = "Test workstation", SerialNumber = "TEST-SERIAL-NOT-REAL",
            Windows = "Windows 11 Pro", LastBoot = "2026-11-08T10:00:00Z", UptimeSeconds = 86400,
            InstalledRamBytes = 17179869184, VisibleRamBytes = 17179869184, FreeRamBytes = 8589934592,
            Cpus = [new() { Name = "Synthetic CPU", Cores = 8, LogicalProcessors = 16, LoadPercent = 25 }],
            Volumes = [new() { Drive = "C:", FileSystem = "NTFS", SizeBytes = 107374182400, FreeBytes = 53687091200 }],
            PhysicalDisks = [new() { Name = "Synthetic SSD", Health = "Healthy", OperationalStatus = "OK", SizeBytes = 107374182400 }],
            Updates = [new() { Id = "KB-TEST-ONLY", Description = "Synthetic security update", InstalledOn = "2026-11-08" }],
            RebootPending = true, RebootReasons = ["Windows Update ожидает перезагрузку"],
            Sections = new[] { "computer", "performance", "volumes", "physical", "updates", "restart" }.ToDictionary(k => k, _ => "ok")
        };
        var reply = new MaintenanceResult { Code = 0, Completed = true, Computer = "TEST-PC", SystemInfo = data };
        int queries = 0;
        using var dialog = new SystemInfoDialog("TEST-PC", _ => { queries++; return Task.FromResult((reply, 0, "")); }, autoLoad: false);
        dialog.Show(); Application.DoEvents();
        await dialog.RefreshData();
        Check(queries == 1 && dialog.RefreshEnabled, "Additional info request did not complete");
        var tables = dialog.Tables.ToArray();
        Check(tables.Length == 4 && tables.All(g => g.ReadOnly && !g.AllowUserToAddRows && !g.AllowUserToDeleteRows
            && g.ClipboardCopyMode == DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText), "Additional tables must be read-only and copyable");
        Check(tables.Sum(g => g.Rows.Count) >= 14, "Requested information missing from result");
        Check(SystemInfoDialog.Bytes(null).Contains("Не удалось") && SystemInfoDialog.Percent(null).Contains("Не удалось"), "Unknown numeric values must not become zero");
        Check(SystemInfoDialog.Uptime(86400) == "1 д 00:00:00" && SystemInfoDialog.Uptime(null).Contains("Не удалось"), "Uptime formatting must preserve unknown state");
        Check(SystemInfoDialog.RebootText(false).Contains("не обнаружено") && SystemInfoDialog.RebootText(null).Contains("Не удалось"), "Missing reboot state is not a false no-reboot claim");
        var general = tables[0]; general.SelectAll();
        var copy = general.GetClipboardContent();
        Check(copy != null && copy.GetText().Contains("TEST-SERIAL-NOT-REAL"), "Read-only table copy must contain full values");
        dialog.SaveSmokeImages(output);
        data.Sections["physical"] = "error"; data.PhysicalDisks = [];
        data.SectionErrors["physical"] = ["Access denied (test fixture)"];
        await dialog.RefreshData();
        Check(dialog.StatusText.Contains("Часть сведений"), "One failed section must not discard the rest");
        var pending = new TaskCompletionSource<(MaintenanceResult, int, string)>();
        using var waiting = new SystemInfoDialog("TEST-PC", _ => pending.Task, autoLoad: false);
        waiting.Show(); Application.DoEvents();
        var active = waiting.RefreshData();
        Check(!waiting.RefreshEnabled && ReferenceEquals(active, waiting.RefreshData()), "Repeated refresh must reuse the in-flight request");
        pending.SetResult((new MaintenanceResult { Code = 5, Message = "Access denied", Completed = true }, 5, ""));
        await active;
        Check(waiting.StatusText.Contains("недоступны") && waiting.RefreshEnabled, "Remote denial must be shown without stale values");
        Check(waiting.Tables.All(g => g.Rows.Count == 0), "Failure must not retain previous computer data");
        waiting.Close(); dialog.Close();
        File.WriteAllText(Path.Combine(output, "system-info-checks.txt"), "PASS: all additional categories, readonly table copy, unknown numeric/restart states, partial failures, access denial and refresh single-flight. Synthetic data only; no remote changes.");
    }
}
