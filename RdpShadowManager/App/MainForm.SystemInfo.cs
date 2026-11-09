namespace RdpShadowManager;
internal sealed partial class MainForm
{
    private readonly Button moreSystemInfo = new() { Text = "Дополнительно…", AutoSize = true, Enabled = false };
    private void OpenSystemInfo()
    {
        if (busy) return;
        string computer;
        try { computer = RdpShadowManager.Core.Connection.ValidateHost(host.Text); }
        catch (Exception ex) { Failure("Проверка цели сведений", host.Text, ex); return; }
        using var dialog = new SystemInfoDialog(computer,
            token => MaintenanceClient.Run(new MaintenanceRequest("SystemInfo", computer, MaintenanceClient.LocalMachineId(), 0, "", ""), token),
            (result, code, stderr) =>
            {
                if (IsDisposed) return;
                Trace("Дополнительные сведения", computer, result.Report(), code);
                TracePowerShellStreams("Дополнительные сведения", computer, stderr, code);
            });
        dialog.ShowDialog(this);
    }
}
