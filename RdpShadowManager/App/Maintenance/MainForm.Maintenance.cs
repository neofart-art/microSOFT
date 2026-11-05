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
        serviceNotice.Text = $"{text}\nКомпьютер: {computer}   Пользователь: {user}" + (finished ? $"   Завершено: {DateTimeOffset.Now:dd.MM.yyyy HH:mm:ss zzz}" : "");
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
        progress.Visible = true; UpdateConnect();
        status.Text = "WinRM: проверка компьютера, сеанса, SID, профиля и версий Outlook…";
        bool mutationSent = false;
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
            request = request with { Action = dialog.IsRestore ? "Restore" : "Apply", Versions = dialog.SelectedVersions,
                CloseOutlook = dialog.CloseOutlook, BackupId = dialog.BackupId, OperationId = Guid.NewGuid().ToString() };
            Trace("Начало обслуживания", computer, $"{request.Action}; пользователь={inspection.User}; SID={inspection.Sid}; сеанс={selected.Id}; ID операции={request.OperationId}");
            status.Text = "Выполняется удалённое обслуживание. Дождитесь проверки результата…";
            ShowServiceNotice(dialog.IsRestore ? "Восстановление настроек AutoDiscover…" : "Применение настроек AutoDiscover…", SystemColors.ControlText, computer, inspection.User, false);
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
            ShowServiceNotice(!confirmed ? "Результат выполнения не подтверждён"
                : success ? dialog.IsRestore ? "Предыдущие настройки AutoDiscover успешно восстановлены" : "Настройки Outlook AutoDiscover успешно применены"
                : partial ? dialog.IsRestore ? "Настройки восстановлены частично. Подробнее — в диагностике" : "Настройки применены частично. Подробнее — в диагностике"
                : dialog.IsRestore ? "Не удалось восстановить настройки. Подробнее — в диагностике" : "Не удалось применить настройки. Подробнее — в диагностике",
                success ? Color.ForestGreen : partial || !confirmed ? Color.DarkOrange : Color.Firebrick, computer, inspection.User);
            status.Text = success ? $"Помощник завершён: изменено {finished.Result.Success}, пропущено {finished.Result.Skipped}, ошибок {finished.Result.Failed}. Значения проверены." : "Обслуживание завершилось с ошибками. См. отчёт и резервную копию.";
            MaintenanceDialog.ShowReport(this, "Результат обслуживания AutoDiscover", finished.Result.Report());
        }
        catch (TimeoutException ex) { serviceUncertain = mutationSent; ShowServiceNotice(mutationSent ? "Результат выполнения не подтверждён" : "Не удалось применить настройки. Подробнее — в диагностике", mutationSent ? Color.DarkOrange : Color.Firebrick, computer, $"{selected.Domain}\\{selected.User}"); Failure("WinRM: результат неизвестен", computer, ex); MaintenanceDialog.ShowReport(this, "Результат операции неизвестен", ex.Message); }
        catch (Exception ex)
        {
            if (mutationSent) serviceUncertain = true;
            ShowServiceNotice(mutationSent ? "Результат выполнения не подтверждён" : "Не удалось применить настройки. Подробнее — в диагностике", mutationSent ? Color.DarkOrange : Color.Firebrick, computer, $"{selected.Domain}\\{selected.User}");
            var title = mutationSent ? "Результат операции неизвестен. Проверьте удалённую резервную копию." : "Обслуживание не начато";
            Failure("WinRM обслуживание", computer, ex); MaintenanceDialog.ShowReport(this, title, ex.Message);
        }
        finally
        {
            busy = servicing = false; progress.Visible = false; elevate.Enabled = !isElevated;
            host.Enabled = grid.Enabled = refresh.Enabled = consent.Enabled = noConsent.Enabled = timeout.Enabled = true;
            UpdateConnect();
        }
    }
}

