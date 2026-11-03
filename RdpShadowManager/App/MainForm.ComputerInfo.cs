using System.Net;
using RdpShadowManager.Core;

namespace RdpShadowManager;
internal sealed partial class MainForm
{
    private readonly Label computerInfo = new() { Text = "Получите сеансы выбранного компьютера.", AutoSize = true, Dock = DockStyle.Top, Margin = new(3, 6, 3, 6), UseMnemonic = false };
    private readonly Button infoRefresh = new() { Text = "↻", AccessibleName = "Обновить сведения о ПК", AutoSize = true, Enabled = false };
    private readonly ToolTip infoTips = new() { AutoPopDelay = 30000 };
    private CancellationTokenSource? infoCancellation;
    private long infoGeneration;
    private bool infoAvailable;
    internal Func<string, CancellationToken, Task<(MaintenanceResult Result, int ExitCode, string StandardError)>> InfoQuery =
        (computer, token) => MaintenanceClient.Run(new MaintenanceRequest("ComputerInfo", computer, MaintenanceClient.LocalMachineId(), 0, "", ""), token);
    internal Func<string, CancellationToken, Task<IPAddress[]>> InfoDns = (computer, token) => Dns.GetHostAddressesAsync(computer, token);
    private Control BuildComputerInfo()
    {
        var group = new GroupBox { Text = "Сведения о ПК", Dock = DockStyle.Top, AutoSize = true, Padding = new(6) };
        var rows = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 2 };
        rows.ColumnStyles.Add(new(SizeType.Percent, 100));
        rows.Controls.Add(infoRefresh, 0, 0); rows.Controls.Add(computerInfo, 0, 1);
        rows.SizeChanged += (_, _) => computerInfo.MaximumSize = new(Math.Max(40, rows.ClientSize.Width - 6), 0);
        group.Controls.Add(rows);
        infoRefresh.Click += async (_, _) => await LoadComputerInfo();
        return group;
    }
    private void ClearComputerInfo()
    {
        infoGeneration++; infoCancellation?.Cancel(); infoAvailable = false; infoRefresh.Enabled = false;
        computerInfo.Text = "Получите сеансы выбранного компьютера."; infoTips.SetToolTip(computerInfo, "");
    }
    private async Task LoadComputerInfo()
    {
        string computer;
        try { computer = Connection.ValidateHost(host.Text); }
        catch { return; }
        infoCancellation?.Cancel();
        var cancellation = new CancellationTokenSource(); infoCancellation = cancellation;
        long generation = ++infoGeneration;
        infoAvailable = true; infoRefresh.Enabled = false; computerInfo.Text = "Получение сведений…";
        bool Current() => !IsDisposed && !Disposing && !closing && generation == infoGeneration;
        try
        {
            MaintenanceResult? data = null;
            try
            {
                var reply = await InfoQuery(computer, cancellation.Token);
                if (!Current()) return;
                Trace("Сведения о ПК / WinRM", computer, reply.Result.Report(), reply.ExitCode);
                if (reply.StandardError.Length > 0) Trace("Сведения о ПК / stderr", computer, reply.StandardError, reply.ExitCode);
                if (reply.ExitCode == 0 && reply.Result.Completed) data = reply.Result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { if (Current()) Trace("Сведения о ПК", computer, ex.ToString(), ex.HResult); }
            if (!Current()) return;
            var addresses = data?.Addresses ?? [];
            string ipLabel = "IP";
            if (addresses.Length == 0)
            {
                try
                {
                    addresses = (await InfoDns(computer, cancellation.Token)).Select(a => a.ToString()).Distinct().ToArray();
                    ipLabel = "IP по DNS";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { if (Current()) Trace("Сведения о ПК / DNS", computer, ex.Message, ex.HResult); }
            }
            if (!Current()) return;
            string Known(string? value) => string.IsNullOrWhiteSpace(value) ? "Не удалось определить" : value;
            var office = data?.Office ?? [];
            var officeText = office.Length > 0 ? string.Join("\n", office) + (data!.OfficeKnown ? "" : "\nНе удалось определить остальные продукты")
                : data?.OfficeKnown == true ? "Office не установлен" : "Не удалось определить";
            computerInfo.Text = $"Компьютер: {computer}" + (data?.Computer.Length > 0 && !string.Equals(computer, data.Computer, StringComparison.OrdinalIgnoreCase) ? $" ({data.Computer})" : "")
                + $"\n{ipLabel}: {(addresses.Length > 0 ? addresses[0] : "Не удалось определить")}" + (addresses.Length > 1 ? $" (+{addresses.Length - 1}, см. подсказку)" : "")
                + $"\nWindows: {Known(data?.Windows)}\nВерсия: {Known(data?.WindowsVersion)}\nOffice: {officeText}";
            infoTips.SetToolTip(computerInfo, addresses.Length == 0 ? "IP не определён" : ipLabel + ":\n" + string.Join("\n", addresses));
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (Current()) infoRefresh.Enabled = infoAvailable;
            if (ReferenceEquals(infoCancellation, cancellation)) infoCancellation = null;
            cancellation.Dispose();
        }
    }
}

internal sealed class ConnectButton : Button
{
    public ConnectButton()
    {
        Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold);
        FlatStyle = FlatStyle.Flat; UseVisualStyleBackColor = false;
        FlatAppearance.BorderSize = 2;
        FlatAppearance.MouseOverBackColor = Color.FromArgb(220, 245, 225);
        FlatAppearance.MouseDownBackColor = Color.FromArgb(180, 225, 190);
        UpdateColors();
    }
    private void UpdateColors()
    {
        FlatAppearance.BorderColor = Enabled ? Color.ForestGreen : SystemColors.ControlDark;
        BackColor = Enabled ? Color.FromArgb(246, 252, 247) : SystemColors.Control;
        ForeColor = Enabled ? SystemColors.ControlText : SystemColors.GrayText;
    }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); UpdateColors(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Focused && Enabled)
        {
            using var pen = new Pen(Color.DarkGreen, Math.Max(1, DeviceDpi / 96f)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            var inset = Math.Max(5, (int)(5 * DeviceDpi / 96f));
            e.Graphics.DrawRectangle(pen, inset, inset, Math.Max(1, Width - inset * 2 - 1), Math.Max(1, Height - inset * 2 - 1));
        }
    }
}
