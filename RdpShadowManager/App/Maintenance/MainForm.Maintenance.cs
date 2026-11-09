using RdpShadowManager.Core;

namespace RdpShadowManager;
internal sealed partial class MainForm
{
    private readonly Button repair = new() { Text = "Исправить Outlook AutoDiscover", AutoSize = true, Enabled = false };
    private readonly Label target = new() { Text = "Цель: выберите активный сеанс.", AutoSize = true, MaximumSize = new(720, 0) };
    private readonly Label serviceNotice = new() { AutoSize = true, Dock = DockStyle.Top, UseMnemonic = false, Visible = false };
    private string serviceNoticeTarget = "";
    private string CurrentServiceTarget => $"{host.Text}|{Selected?.Domain}|{Selected?.User}|{Selected?.Id}";
    private void ClearServiceNotice() { serviceNotice.Text = ""; serviceNotice.Visible = false; serviceNoticeTarget = ""; }
    private void ClearServiceNoticeIfTargetChanged() { if (serviceNoticeTarget != CurrentServiceTarget) ClearServiceNotice(); }
    private void ShowServiceNotice(string text, Color color, string computer, string user, bool finished = true)
    {
        serviceNoticeTarget = CurrentServiceTarget;
        serviceNotice.ForeColor = color;
        serviceNotice.Text = text;
        infoTips.SetToolTip(serviceNotice, $"Компьютер: {computer}; пользователь: {user}; {DateTimeOffset.Now:dd.MM.yyyy HH:mm:ss zzz}");
        serviceNotice.Visible = true;
        if (serviceNotice.Parent is {} parent) serviceNotice.MaximumSize = new(Math.Max(100, parent.ClientSize.Width - 6), 0);
    }
    internal static string MaintenanceOutcome(MaintenanceResult result, int exitCode, string standardError, bool sameOperation)
    {
        if (!result.Completed) return "unknown";
        if (sameOperation && exitCode == 0 && result.Code == 0 && result.Verified && result.Failed == 0
            && result.Errors.Length == 0 && result.Conflicts.Length == 0 && standardError.Length == 0) return "success";
        return sameOperation && result.Success > 0 ? "partial" : "error";
    }
    internal static string MaintenanceTitle(string outcome, bool restore) => outcome switch
    {
        "success" => restore ? "Настройки восстановлены" : "Настройки применены",
        "partial" => restore ? "Восстановлены частично" : "Применены частично",
        "unknown" => "Результат не подтверждён",
        _ => restore ? "Не удалось восстановить" : "Не удалось применить"
    };
    internal static string MaintenanceSummary(MaintenanceResult result, string outcome, bool restore, string computer, string user, string stderr)
    {
        var text = $"{MaintenanceTitle(outcome, restore)}\nКомпьютер: {computer}\nПользователь: {user}\n"
            + $"Изменено: {result.Success}; уже соответствовало/пропущено: {result.Skipped}; ошибок: {result.Failed}.\n";
        if (outcome != "success")
        {
            var reasons = result.Errors.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Take(6).Select(s => s.Length > 500 ? s[..500] + "…" : s).ToArray();
            string reason = reasons.Length > 0 ? string.Join("\n", reasons) :
                result.OutlookDetectionError.Length > 0 ? result.OutlookDetectionError :
                stderr.Length > 0 ? "Транспорт вернул ошибку; подробности сохранены в диагностике." :
                !result.Completed ? "Не получено подтверждение завершения удалённого помощника." :
                !result.Verified ? "Не подтверждена проверка всех значений реестра." :
                result.Code == 0 ? "Ответ не прошёл проверку идентичности операции или целевого пользователя." : result.Message.Length > 0 ? result.Message : "Результат не прошёл проверку кода завершения.";
            text += $"Почему не полностью: {reason}\nКод помощника: {result.Code}.\n";
            if (result.Changed.Length > 0)
                text += "Подтверждённые изменения:\n" + string.Join("\n", result.Changed.Take(5).Select(c =>
                    c.TryGetProperty("Name", out var name) ? name.GetString() : "значение реестра")) + "\n";
        }
        if (result.BackupPath.Length > 0) text += "Резервная копия: " + result.BackupPath + "\n";
        return text + "Подробности — в диагностике.";
    }
    private bool servicing;
    private bool serviceUncertain;
    private async Task Service()
    {
        if (busy || servicing || serviceUncertain || Selected is not { CanConnect: true } selected) return;
        string computer;
        try { computer = Connection.ValidateHost(host.Text); }
        catch (Exception ex) { Failure("Проверка адреса обслуживания", host.Text, ex); return; }
        ClearServiceNotice();
        busy = true; servicing = true;
        host.Enabled = grid.Enabled = refresh.Enabled = consent.Enabled = noConsent.Enabled = timeout.Enabled = false;
        elevate.Enabled = false;
        progress.Visible = false; UpdateConnect();
        status.Text = "";
        bool mutationSent = false;
        bool restoring = false;
        try
        {
            var request = new MaintenanceRequest("Inspect", computer, MaintenanceClient.LocalMachineId(), selected.Id, selected.User, selected.Domain);
            var inspected = await MaintenanceClient.Run(request);
            Trace("WinRM Inspect", computer, inspected.Result.Report(), inspected.ExitCode);
            if (inspected.ExitCode != 0) throw new InvalidOperationException(inspected.Result.Message);
            var inspection = inspected.Result;
            if (inspection.Sid.Length == 0 || inspection.MachineId.Length == 0 || inspection.SessionId != selected.Id)
                throw new InvalidOperationException("Не подтверждена идентичность цели обслуживания.");
            target.Text = $"Компьютер: {computer} ({inspection.Computer})   Пользователь: {inspection.User}\nID сеанса: {inspection.SessionId}   SID: {inspection.Sid}";
            request = request with { Sid = inspection.Sid, MachineId = inspection.MachineId };
            using var dialog = new MaintenanceDialog(computer, inspection, async id =>
            {
                var preview = await MaintenanceClient.Run(request with { Action = "RestorePreview", BackupId = id });
                Trace("WinRM RestorePreview", computer, preview.Result.Report(), preview.ExitCode);
                return preview.Result;
            });
            if (dialog.ShowDialog(this) != DialogResult.OK) { status.Text = "Обслуживание отменено. Настройки не изменены."; return; }
            restoring = dialog.IsRestore;
            request = request with { Action = dialog.IsRestore ? "Restore" : "Apply", Versions = inspection.Versions,
                CloseOutlook = dialog.CloseOutlook, BackupId = dialog.BackupId, OperationId = Guid.NewGuid().ToString() };
            Trace("Начало обслуживания", computer, $"{request.Action}; пользователь={inspection.User}; SID={inspection.Sid}; сеанс={selected.Id}; ID операции={request.OperationId}");
            mutationSent = true;
            var finished = await MaintenanceClient.Run(request);
            Trace("Результат обслуживания", computer, finished.Result.Report(), finished.ExitCode);
            if (finished.StandardError.Length > 0) Trace("WinRM stderr", computer, finished.StandardError, finished.ExitCode);
            var result = finished.Result;
            bool confirmed = result.Completed;
            bool sameOperation = result.OperationId == (dialog.IsRestore ? request.BackupId : request.OperationId)
                && result.MachineId == inspection.MachineId && result.Sid == inspection.Sid;
            var outcome = MaintenanceOutcome(result, finished.ExitCode, finished.StandardError, sameOperation);
            bool success = outcome == "success";
            bool partial = outcome == "partial";
            if (!confirmed) serviceUncertain = true;
            var title = MaintenanceTitle(outcome, dialog.IsRestore);
            ShowServiceNotice(title, success ? Color.ForestGreen : partial || !confirmed ? Color.DarkOrange : Color.Firebrick, computer, inspection.User);
            status.Text = title;
            MessageBox.Show(this, MaintenanceSummary(result, outcome, dialog.IsRestore, computer, inspection.User, finished.StandardError),
                title, MessageBoxButtons.OK, success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            if (mutationSent) serviceUncertain = true;
            string outcome = mutationSent ? "unknown" : "error";
            var title = MaintenanceTitle(outcome, restoring);
            ShowServiceNotice(title, mutationSent ? Color.DarkOrange : Color.Firebrick, computer, $"{selected.Domain}\\{selected.User}");
            status.Text = title;
            Trace("Обслуживание AutoDiscover", computer, ex.ToString(), ex.HResult);
            MessageBox.Show(this, title + "\nКомпьютер: " + computer + "\nПричина: " + ex.Message + "\nПодробнее — в диагностике.",
                title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            busy = servicing = false; progress.Visible = false; elevate.Enabled = !isElevated;
            host.Enabled = grid.Enabled = refresh.Enabled = consent.Enabled = noConsent.Enabled = timeout.Enabled = true;
            UpdateConnect();
        }
    }
}

