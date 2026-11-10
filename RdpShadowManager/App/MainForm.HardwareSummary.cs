namespace RdpShadowManager;
internal sealed partial class MainForm
{
    private readonly Dictionary<string, TextBox> hardwareFields = [];
    private Control BuildHardwareSummary()
    {
        var group = new GroupBox { Text = "Компьютер и время работы", Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new(6) };
        var rows = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 4 };
        rows.ColumnStyles.Add(new(SizeType.Percent, 50)); rows.ColumnStyles.Add(new(SizeType.Percent, 50));
        for (int i = 0; i < 4; i++) rows.RowStyles.Add(new(SizeType.AutoSize));
        void Add(string key, string caption, int column, int row, int span = 1)
        {
            var card = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1, RowCount = 2, Margin = new(4, 6, 4, 6) };
            card.ColumnStyles.Add(new(SizeType.Percent, 100));
            card.RowStyles.Add(new(SizeType.AutoSize)); card.RowStyles.Add(new(SizeType.AutoSize));
            var field = new ReadableInfoField { Text = "Не удалось определить", AccessibleName = caption, Margin = new(0, 3, 0, 0) };
            card.Controls.Add(new Label { Text = caption, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = Padding.Empty }, 0, 0);
            card.Controls.Add(field, 0, 1); rows.Controls.Add(card, column, row); rows.SetColumnSpan(card, span);
            hardwareFields[key] = field;
        }
        Add("boot", "Последняя загрузка", 0, 0); Add("uptime", "Время работы", 1, 0);
        Add("model", "Модель компьютера", 0, 1, 2);
        Add("serial", "Серийный номер", 0, 2); Add("ram", "Оперативная память", 1, 2);
        Add("cpu", "Процессор", 0, 3, 2);
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
