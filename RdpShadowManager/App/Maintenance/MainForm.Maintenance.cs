using RdpShadowManager.Core;

namespace RdpShadowManager;
internal sealed partial class MainForm
{
    private readonly Button repair = new() { Text = "Исправить Outlook AutoDiscover", AutoSize = true, Enabled = false };
    private readonly Label target = new() { Text = "Цель: выберите активный сеанс.", AutoSize = true, MaximumSize = new(720, 0) };
    private bool servicing;
    private bool serviceUncertain;
    private async Task Service()
    {
        if (busy || servicing || serviceUncertain || Selected is not { CanConnect: true } selected) return;
        string computer;
        try { computer = Connection.ValidateHost(host.Text); }
        catch (Exception ex) { Failure("Проверка адреса обслуживания", host.Text, ex); return; }
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
            mutationSent = true;
            var finished = await MaintenanceClient.Run(request);
            Trace("Результат обслуживания", computer, finished.Result.Report(), finished.ExitCode);
            if (finished.StandardError.Length > 0) Trace("WinRM stderr", computer, finished.StandardError, finished.ExitCode);
            status.Text = finished.ExitCode == 0 ? $"Помощник завершён: изменено {finished.Result.Success}, пропущено {finished.Result.Skipped}, ошибок {finished.Result.Failed}. Значения проверены." : "Обслуживание завершилось с ошибками. См. отчёт и резервную копию.";
            MaintenanceDialog.ShowReport(this, "Результат обслуживания AutoDiscover", finished.Result.Report());
        }
        catch (TimeoutException ex) { serviceUncertain = true; Failure("WinRM: результат неизвестен", computer, ex); MaintenanceDialog.ShowReport(this, "Результат операции неизвестен", ex.Message); }
        catch (Exception ex)
        {
            if (mutationSent) serviceUncertain = true;
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
