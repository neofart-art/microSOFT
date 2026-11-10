using System.Globalization;
using RdpShadowManager.Core;

namespace RdpShadowManager;
internal sealed class SystemInfoDialog : Form
{
    private readonly string computer;
    private readonly Func<CancellationToken, Task<(MaintenanceResult Result, int ExitCode, string StandardError)>> query;
    private readonly Action<MaintenanceResult, int, string> log;
    private readonly Button refresh = new() { Text = "Обновить", AutoSize = true };
    private readonly Label status = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<string, TextBox> notes = [];
    private readonly Dictionary<string, DataGridView> grids = [];
    private CancellationTokenSource? cancellation;
    private Task? current;
    private bool loading, closeRequested;
    internal bool RefreshEnabled => refresh.Enabled;
    internal string StatusText => status.Text;
    internal IEnumerable<DataGridView> Tables => grids.Values;
    public SystemInfoDialog(string computer,
        Func<CancellationToken, Task<(MaintenanceResult Result, int ExitCode, string StandardError)>> query,
        Action<MaintenanceResult, int, string>? log = null, bool autoLoad = true)
    {
        this.computer = Connection.ValidateHost(computer); this.query = query;
        this.log = log ?? ((_, _, _) => { });
        Text = "Дополнительные сведения — " + this.computer;
        AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new(1000, 680); MinimumSize = new(760, 540);
        StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new(10) };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        Controls.Add(layout);
        var bar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        bar.Controls.Add(new Label { Text = "Цель зафиксирована: " + this.computer, AutoSize = true, MaximumSize = new(760, 0), Margin = new(3,8,12,3) });
        bar.Controls.Add(refresh);
        layout.Controls.Add(bar, 0, 0); layout.Controls.Add(status, 0, 1); layout.Controls.Add(tabs, 0, 2);
        AddTab("general", "Компьютер и нагрузка", [("general", new[] { "Параметр", "Значение" })]);
        AddTab("disks", "Диски", [
            ("volumes", new[] { "Локальный диск", "Файловая система", "Размер", "Свободно", "Свободно, %" }),
            ("physical", new[] { "Физический диск", "Размер", "Состояние Windows", "Операционное состояние" })]);
        AddTab("updates", "Обновления и перезагрузка", [("updates", new[] { "Обновление", "Описание", "Установлено" })]);
        refresh.Click += async (_,_) => await RefreshData();
        Shown += async (_,_) => { if (autoLoad) await RefreshData(); };
        FormClosing += async (_,e) =>
        {
            if (!loading) return;
            e.Cancel = true;
            if (closeRequested) return;
            closeRequested = true; cancellation?.Cancel();
            if (current != null) await current;
            if (!IsDisposed) Close();
        };
    }
    private void AddTab(string key, string title, (string Key, string[] Columns)[] tables)
    {
        var page = new TabPage(title); tabs.TabPages.Add(page);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = tables.Length + 1, Padding = new(6) };
        layout.RowStyles.Add(new(SizeType.Absolute, key == "updates" ? 130 : 85));
        var note = new TextBox { ReadOnly = true, Multiline = true, WordWrap = true, ScrollBars = ScrollBars.Vertical, ShortcutsEnabled = true, Dock = DockStyle.Fill, BackColor = SystemColors.Window };
        notes[key] = note; layout.Controls.Add(note, 0, 0); page.Controls.Add(layout);
        for (int i=0; i<tables.Length; i++)
        {
            layout.RowStyles.Add(new(SizeType.Percent, 100f / tables.Length));
            var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                RowHeadersVisible = false, MultiSelect = true, SelectionMode = DataGridViewSelectionMode.CellSelect,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells };
            grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            foreach (var column in tables[i].Columns) grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = column, SortMode = DataGridViewColumnSortMode.NotSortable });
            grids[tables[i].Key] = grid; layout.Controls.Add(grid, 0, i+1);
        }
    }
    internal Task RefreshData()
    {
        if (loading) return current ?? Task.CompletedTask;
        current = ReadAsync(); return current;
    }
    private async Task ReadAsync()
    {
        loading = true; refresh.Enabled = false;
        cancellation = new();
        foreach (var grid in grids.Values) grid.Rows.Clear();
        foreach (var note in notes.Values) note.Text = "Получение сведений…";
        status.Text = "Чтение сведений выбранного ПК. Изменения на нём не выполняются.";
        try
        {
            var reply = await query(cancellation.Token);
            log(reply.Result, reply.ExitCode, reply.StandardError);
            if (closeRequested || IsDisposed) return;
            if (reply.ExitCode != 0 || reply.Result.Code != 0 || !reply.Result.Completed || reply.Result.SystemInfo == null
                || PowerShellDiagnostics.Analyze(reply.StandardError).Errors.Length > 0)
                throw new InvalidOperationException(reply.Result.Message.Length > 0 ? reply.Result.Message : "Нет подтверждённого ответа со сведениями.");
            Display(reply.Result.SystemInfo);
        }
        catch (OperationCanceledException) { if (!closeRequested) ShowUnavailable("Запрос отменён."); }
        catch (Exception ex)
        {
            log(new MaintenanceResult { Code = 1, Message = ex.Message, Errors = [ex.ToString()] }, 1, "");
            if (!closeRequested && !IsDisposed) ShowUnavailable(ex.Message);
        }
        finally
        {
            cancellation.Dispose(); cancellation = null; loading = false;
            if (!IsDisposed) refresh.Enabled = !closeRequested;
        }
    }
    private void ShowUnavailable(string message)
    {
        foreach (var note in notes.Values) note.Text = "Не удалось определить: " + message;
        status.Text = "Сведения недоступны. Причина сохранена в диагностике.";
    }
    internal static string Bytes(long? bytes) => bytes is >= 0 ? $"{bytes.Value / 1073741824d:N2} ГиБ" : "Не удалось определить";
    internal static string Percent(double? value) => value is >= 0 and <= 100 ? $"{value.Value:N1} %" : "Не удалось определить";
    internal static string Uptime(double? seconds)
    {
        if (seconds is not >= 0 || seconds >= TimeSpan.MaxValue.TotalSeconds) return "Не удалось определить";
        var value = TimeSpan.FromSeconds(seconds.Value);
        return $"{value.Days} д {value.Hours:00}:{value.Minutes:00}:{value.Seconds:00}";
    }
    private static string Known(string? value) => string.IsNullOrWhiteSpace(value) ? "Не удалось определить" : value;
    internal static string RebootText(bool? pending) => pending switch
    {
        true => "Есть признаки ожидающей перезагрузки",
        false => "Признаков ожидающей перезагрузки не обнаружено",
        null => "Не удалось определить, ожидается ли перезагрузка"
    };
    private string SectionNotes(SystemInfoData data, params string[] sections) => string.Join(Environment.NewLine, sections.Select(s =>
    {
        var state = data.Sections.GetValueOrDefault(s, "unknown");
        var errors = data.SectionErrors.GetValueOrDefault(s, []).Where(e => !string.IsNullOrWhiteSpace(e));
        return $"{s}: {(state == "ok" ? "прочитано" : "не полностью доступно")} " + string.Join("; ", errors);
    }));
    private void Display(SystemInfoData data)
    {
        string captured = DateTimeOffset.TryParse(data.CapturedAt, out var moment) ? moment.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss zzz") : "не определено";
        status.Text = $"Компьютер: {computer}. Снимок: {captured}. " +
            (data.Sections.Values.Any(s => s != "ok") ? "Часть сведений недоступна — см. причины на вкладках." : "Сведения прочитаны. Это снимок, не мониторинг в реальном времени.");
        void Row(string key, params object?[] values) => grids[key].Rows.Add(values);
        Row("general", "Производитель", Known(data.Manufacturer)); Row("general", "Модель", Known(data.Model));
        Row("general", "Серийный номер BIOS", Known(data.SerialNumber)); Row("general", "Windows", Known(data.Windows));
        Row("general", "Последняя загрузка", DateTimeOffset.TryParse(data.LastBoot, out var boot) ? boot.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss zzz") : "Не удалось определить");
        Row("general", "Время работы Windows", Uptime(data.UptimeSeconds));
        Row("general", "Физическая ОЗУ", Bytes(data.InstalledRamBytes)); Row("general", "ОЗУ, доступная Windows", Bytes(data.VisibleRamBytes));
        Row("general", "Свободная ОЗУ", Bytes(data.FreeRamBytes));
        Row("general", "Занято ОЗУ", data.VisibleRamBytes is > 0 && data.FreeRamBytes.HasValue ? Percent(100d*(data.VisibleRamBytes.Value-data.FreeRamBytes.Value)/data.VisibleRamBytes.Value) : "Не удалось определить");
        if (data.Cpus.Length == 0) Row("general", "Процессор", "Не удалось определить");
        foreach (var cpu in data.Cpus) Row("general", Known(cpu.Name), $"Ядер: {cpu.Cores?.ToString() ?? "?"}; логических: {cpu.LogicalProcessors?.ToString() ?? "?"}; загрузка: {Percent(cpu.LoadPercent)}");
        foreach (var volume in data.Volumes) Row("volumes", Known(volume.Drive), Known(volume.FileSystem), Bytes(volume.SizeBytes), Bytes(volume.FreeBytes),
            volume.SizeBytes is > 0 && volume.FreeBytes.HasValue ? Percent(100d*volume.FreeBytes.Value/volume.SizeBytes.Value) : "Не удалось определить");
        foreach (var disk in data.PhysicalDisks) Row("physical", Known(disk.Name), Bytes(disk.SizeBytes), Known(disk.Health), Known(disk.OperationalStatus));
        foreach (var update in data.Updates) Row("updates", Known(update.Id), Known(update.Description), Known(update.InstalledOn));
        notes["general"].Text = "Снимок CPU/ОЗУ; «Обновить» выполняет новый запрос.\r\n" + SectionNotes(data, "computer", "performance");
        notes["disks"].Text = "Только локальные фиксированные диски. Состояние Storage/Windows не заменяет полную SMART-диагностику.\r\n" + SectionNotes(data, "volumes", "physical");
        notes["updates"].Text = RebootText(data.RebootPending) + "\r\n" + string.Join("\r\n", data.RebootReasons) +
            "\r\nПоследние 20 QFE-обновлений Windows; это не полная история Windows Update, Office или Store.\r\n" + SectionNotes(data, "updates", "restart");
    }
    internal void SaveSmokeImages(string folder)
    {
        for (int i=0; i<tabs.TabPages.Count; i++)
        {
            tabs.SelectedIndex = i; Application.DoEvents();
            using var image = new Bitmap(Width, Height);
            DrawToBitmap(image, ClientRectangle);
            image.Save(Path.Combine(folder, $"system-info-{i+1}.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
    }
}
