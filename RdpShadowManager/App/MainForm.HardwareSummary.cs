namespace RdpShadowManager;
internal sealed partial class MainForm
{
    private readonly Dictionary<string, TextBox> hardwareFields = [];
    private Control BuildHardwareSummary()
    {
        var group = new GroupBox { Text = "Компьютер и время работы", Dock = DockStyle.Top, AutoSize = true, Padding = new(6) };
        var rows = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        rows.ColumnStyles.Add(new(SizeType.Absolute, 110)); rows.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var (key, caption, height) in new[] { ("boot","Загрузка Windows",32),("uptime","Время работы",24),("model","Модель ПК",32),("serial","Серийный номер",24),("cpu","CPU",44),("ram","ОЗУ",24) })
        {
            int row = rows.RowCount++;
            var field = new TextBox { ReadOnly = true, Multiline = true, WordWrap = true, ShortcutsEnabled = true, ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill, Height = height, MinimumSize = new(40,height), Text = "Не удалось определить", BackColor = SystemColors.Window };
            rows.RowStyles.Add(new(SizeType.Absolute, height+6));
            rows.Controls.Add(new Label { Text = caption, AutoSize = true, Dock = DockStyle.Fill }, 0, row);
            rows.Controls.Add(field, 1, row); hardwareFields[key] = field;
        }
        group.Controls.Add(rows); return group;
    }
    private void ShowHardwareSummary(SystemInfoData? data)
    {
        if (hardwareFields.Count == 0) return;
        string Known(string? value) => string.IsNullOrWhiteSpace(value) ? "Не удалось определить" : value;
        hardwareFields["boot"].Text = DateTimeOffset.TryParse(data?.LastBoot, out var boot) ? boot.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss zzz") : "Не удалось определить";
        hardwareFields["uptime"].Text = SystemInfoDialog.Uptime(data?.UptimeSeconds);
        hardwareFields["model"].Text = Known(data?.Model);
        hardwareFields["serial"].Text = Known(data?.SerialNumber);
        hardwareFields["cpu"].Text = data?.Cpus.Length > 0 ? string.Join(Environment.NewLine, data.Cpus.Select(c => Known(c.Name))) : "Не удалось определить";
        hardwareFields["ram"].Text = SystemInfoDialog.Bytes(data?.InstalledRamBytes);
    }
}
