using System.Net;
using RdpShadowManager.Core;

namespace RdpShadowManager;
internal sealed partial class MainForm
{
    private static TextBox InfoField(int height) => new ReadableInfoField();
    private readonly TextBox infoComputer = InfoField(60);
    private readonly TextBox infoAddresses = InfoField(60);
    private readonly TextBox infoWindows = InfoField(54);
    private readonly TextBox infoOffice = InfoField(90);
    private readonly Label ipCaption = new() { Text = "IPv4", AutoSize = true };
    private readonly Button infoRefresh = new() { Text = "↻", AccessibleName = "Обновить сведения о ПК", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Enabled = false };
    private readonly ToolTip infoTips = new() { AutoPopDelay = 30000 };
    private CancellationTokenSource? infoCancellation;
    private long infoGeneration;
    private bool infoAvailable;
    internal Func<string, CancellationToken, Task<(MaintenanceResult Result, int ExitCode, string StandardError)>> InfoQuery =
        (computer, token) => MaintenanceClient.Run(new MaintenanceRequest("ComputerInfo", computer, MaintenanceClient.LocalMachineId(), 0, "", "", ClientIPv4: RdpConnections.LocalIPv4Addresses()), token);
    internal Func<string, CancellationToken, Task<IPAddress[]>> InfoDns = (computer, token) => Dns.GetHostAddressesAsync(computer, token);
    private Control BuildComputerInfo()
    {
        var group = new GroupBox { Text = "Сведения о ПК", Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new(6) };
        var rows = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 4 };
        rows.ColumnStyles.Add(new(SizeType.Percent, 50)); rows.ColumnStyles.Add(new(SizeType.Percent, 50));
        for (int i = 0; i < 4; i++) rows.RowStyles.Add(new(SizeType.AutoSize));
        Control Field(Control caption, TextBox value)
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2, Margin = new(3, 4, 3, 4) };
            panel.ColumnStyles.Add(new(SizeType.Percent, 100));
            panel.RowStyles.Add(new(SizeType.AutoSize)); panel.RowStyles.Add(new(SizeType.AutoSize));
            panel.Controls.Add(caption, 0, 0); panel.Controls.Add(value, 0, 1);
            return panel;
        }
        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        tools.Controls.Add(infoRefresh); tools.Controls.Add(moreSystemInfo);
        rows.Controls.Add(tools, 0, 0); rows.SetColumnSpan(tools, 2);
        moreSystemInfo.Click += (_,_) => OpenSystemInfo();
        rows.Controls.Add(Field(new Label { Text = "Компьютер", AutoSize = true }, infoComputer), 0, 1);
        rows.Controls.Add(Field(ipCaption, infoAddresses), 1, 1);
        var windows = Field(new Label { Text = "Windows", AutoSize = true }, infoWindows);
        var office = Field(new Label { Text = "Office", AutoSize = true }, infoOffice);
        rows.Controls.Add(windows, 0, 2); rows.SetColumnSpan(windows, 2);
        rows.Controls.Add(office, 0, 3); rows.SetColumnSpan(office, 2);
        group.Controls.Add(rows);
        infoRefresh.Click += async (_, _) => await LoadComputerInfo();
        SetComputerDetails("", "Получите сеансы выбранного компьютера.", "", "");
        return group;
    }
    private void SetComputerDetails(string computer, string addresses, string windows, string office)
    {
        infoComputer.Text = computer; infoAddresses.Text = addresses;
        infoWindows.Text = windows; infoOffice.Text = office;
    }
    private void ShowComputerIPv4(IPv4Choice choice)
    {
        infoAddresses.Text = choice.Address.Length > 0 ? "IPv4: " + choice.Address : "IPv4: Не удалось определить";
        ipCaption.Text = "IPv4";
        infoTips.SetToolTip(infoAddresses, choice.Source);
    }
    private void ClearComputerInfo()
    {
        ShowHardwareSummary(null);
        infoGeneration++; rdpPolling?.Cancel(); currentRemoteAddresses = []; infoCancellation?.Cancel(); infoAvailable = false; infoRefresh.Enabled = false;
        SetComputerDetails("", "Получите сеансы выбранного компьютера.", "", ""); ipCaption.Text = "IPv4"; infoTips.SetToolTip(infoAddresses, "");
    }
    private async Task LoadComputerInfo()
    {
        string computer;
        try { computer = Connection.ValidateHost(host.Text); }
        catch { return; }
        infoCancellation?.Cancel();
        var cancellation = new CancellationTokenSource(); infoCancellation = cancellation;
        long generation = ++infoGeneration;
        infoAvailable = true; infoRefresh.Enabled = false; SetComputerDetails(computer, "Получение сведений…", "Получение сведений…", "Получение сведений…");
        bool Current() => !IsDisposed && !Disposing && !closing && generation == infoGeneration;
        try
        {
            MaintenanceResult? data = null;
            try
            {
                var reply = await InfoQuery(computer, cancellation.Token);
                if (!Current()) return;
                Trace("Сведения о ПК / WinRM", computer, reply.Result.Report(), reply.ExitCode);
                TracePowerShellStreams("Сведения о ПК", computer, reply.StandardError, reply.ExitCode);
                if (reply.ExitCode == 0 && reply.Result.Completed) data = reply.Result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { if (Current()) Trace("Сведения о ПК", computer, ex.ToString(), ex.HResult); }
            if (!Current()) return;
            currentRemoteAddresses = data?.Addresses ?? [];
            string? connected = null;
            try { connected = await InfoConnectedIPv4(computer, ComputerIPv4.Addresses(currentRemoteAddresses), cancellation.Token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { if (Current()) Trace("IPv4 подключения", computer, ex.Message, ex.HResult); }
            var selectedIPv4 = ComputerIPv4.Select(connected, data?.PrimaryIPv4, currentRemoteAddresses, data?.RdpIPv4);
            if (!Current()) return;
            string Known(string? value) => string.IsNullOrWhiteSpace(value) ? "Не удалось определить" : value;
            var office = data?.Office ?? [];
            var officeText = office.Length > 0 ? string.Join("\n", office) + (data!.OfficeKnown ? "" : "\nНе удалось определить остальные продукты")
                : data?.OfficeKnown == true ? "Office не установлен" : "Не удалось определить";
            SetComputerDetails(computer + (data?.Computer.Length > 0 && !string.Equals(computer, data.Computer, StringComparison.OrdinalIgnoreCase) ? $" ({data.Computer})" : ""),
                "", Known(data?.Windows) + Environment.NewLine + Known(data?.WindowsVersion), officeText.Replace("\n", Environment.NewLine));
            ShowComputerIPv4(selectedIPv4);
            ShowHardwareSummary(data?.SystemInfo);

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
