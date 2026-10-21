using System.ComponentModel;
using System.Net.Sockets;
using RdpShadowManager.Core;

namespace RdpShadowManager;
internal sealed partial class MainForm
{
    private readonly Button restartComputer = new() { Text = "Перезагрузить компьютер", Width = 176, Height = 48 };
    private readonly Button shutdownComputer = new() { Text = "Выключить компьютер", Width = 176, Height = 48 };
    private readonly Button abortPower = new() { Text = "Отменить запланированное действие", Width = 176, Height = 60 };
    private readonly Label powerTarget = new() { AutoSize = true, MaximumSize = new(176, 0) };
    private bool powering;
    private GroupBox BuildPowerGroup()
    {
        var group = new GroupBox { Text = "Питание удалённого компьютера", Dock = DockStyle.Fill, AutoSize = true, Padding = new(8), Margin = new(0,12,0,0) };
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        controls.Controls.AddRange([powerTarget, restartComputer, shutdownComputer, abortPower]); group.Controls.Add(controls);
        restartComputer.Click += async (_,_) => await SendPower(PowerAction.Restart);
        shutdownComputer.Click += async (_,_) => await SendPower(PowerAction.Shutdown);
        abortPower.Click += async (_,_) => await SendPower(PowerAction.Abort);
        UpdatePowerButtons(); return group;
    }
    private void UpdatePowerButtons()
    {
        restartComputer.Enabled = shutdownComputer.Enabled = abortPower.Enabled = !busy && !string.IsNullOrWhiteSpace(host.Text);
        powerTarget.Text = string.IsNullOrWhiteSpace(host.Text) ? "Цель: введите удалённый компьютер." : $"Цель: {host.Text}";
    }
    private async Task SendPower(PowerAction action)
    {
        if (busy || powering) return;
        string computer;
        try { computer = Connection.ValidateHost(host.Text); }
        catch (Exception ex) { Failure("Проверка цели питания", host.Text, ex); return; }
        powering = busy = true; host.Enabled = refresh.Enabled = grid.Enabled = elevate.Enabled = false;
        progress.Visible = true; UpdateConnect();
        string endpoint = computer;
        try
        {
            status.Text = "Проверка адреса удалённого компьютера…";
            var fixedTarget = await Task.Run(() => PowerClient.Resolve(computer));
            endpoint = $"{fixedTarget.Computer} ({fixedTarget.Address})";
            using var dialog = new PowerDialog(fixedTarget, action);
            if (dialog.ShowDialog(this) != DialogResult.OK) { status.Text = $"Запрос питания отменён до отправки. Цель: {endpoint}."; return; }
            var command = dialog.Request;
            Trace("Запрос питания", endpoint, $"Действие={action}; задержка={command.DelaySeconds} с; принудительно={command.Force}; комментарий={System.Text.Json.JsonSerializer.Serialize(command.Comment)}");
            status.Text = $"Отправка запроса питания: {endpoint}…";
            var result = await PowerClient.Send(command);
            Trace($"Питание: {action}", endpoint, result.Message, result.Code);
            status.Text = result.Code == 0 ? result.Message : $"{result.Message} Цель: {endpoint}.";
            if (result.Code != 0) MessageBox.Show(this, status.Text, "Ошибка запроса питания", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (SocketException ex) { PowerError(endpoint, ex.NativeErrorCode, "Не удалось разрешить имя компьютера или получить его адрес. " + ex.Message); }
        catch (TimeoutException ex) { PowerError(endpoint, 1460, ex.Message); }
        catch (Exception ex) { PowerError(endpoint, ex is Win32Exception w ? w.NativeErrorCode : ex.HResult, ex.Message); }
        finally
        {
            busy = powering = false; host.Enabled = refresh.Enabled = grid.Enabled = true;
            elevate.Enabled = !isElevated; progress.Visible = false; UpdateConnect();
        }
    }
    private void PowerError(string endpoint, int code, string message)
    {
        status.Text = $"{message} Цель: {endpoint}. Код: {code}.";
        Trace("Ошибка питания", endpoint, message, code);
        MessageBox.Show(this, status.Text, "Запрос питания не подтверждён", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
