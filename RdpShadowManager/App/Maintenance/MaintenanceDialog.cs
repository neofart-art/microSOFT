namespace RdpShadowManager;
internal sealed class MaintenanceDialog : Form
{
    private readonly RadioButton apply = new() { Text = "Применить настройки", Checked = true, AutoSize = true };
    private readonly RadioButton restore = new() { Text = "Восстановить предыдущие настройки", AutoSize = true };
    private readonly ComboBox backups = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly CheckBox closeOutlook = new() { Text = "Закрыть Outlook в выбранном сеансе", Checked = false, AutoSize = true };
    private readonly Button execute = new() { Text = "Выполнить", AutoSize = true };
    private bool checking;
    public bool IsRestore => restore.Checked;
    public bool CloseOutlook => closeOutlook.Checked;
    public string BackupId => (backups.SelectedItem as BackupChoice)?.Id ?? "";
    public MaintenanceDialog(string computer, MaintenanceResult inspection, Func<string, Task<MaintenanceResult>> preview)
    {
        Text = "Применение предоставленного профиля AutoDiscover";
        AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new(740, 530); MinimumSize = new(650, 520);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 11, Padding = new(12), AutoScroll = true };
        Controls.Add(layout);
        void Add(Control c) { c.Margin = new(3, 6, 3, 6); layout.Controls.Add(c); }
        Add(new Label { Text = $"Компьютер: {computer} ({inspection.Computer})\nПользователь: {inspection.User}\nID сеанса: {inspection.SessionId}    SID: {inspection.Sid}", AutoSize = true });
        Add(new Label { Text = "Это предоставленный профиль настроек, а не универсальное исправление Outlook.\nИзменения HKLM относятся ко всему удалённому компьютеру и могут повлиять на других пользователей.", AutoSize = true, MaximumSize = new(680, 0) });
        Add(apply); Add(restore);
        Add(new Label { Text = inspection.OutlookDetectionError.Length > 0 ? "Версия классического Outlook не определена: " + inspection.OutlookDetectionError + "\nПрименение будет остановлено без изменения реестра. Восстановление из копии остаётся доступным." : "Автоматически определённые ветки Outlook: " + string.Join(", ", inspection.Versions), AutoSize = true, MaximumSize = new(680, 0) });
        Add(new Label { Text = "Резервная копия этого компьютера и SID для восстановления:", AutoSize = true });
        backups.Items.AddRange(inspection.Backups); if (backups.Items.Count > 0) backups.SelectedIndex = 0;
        Add(backups); Add(closeOutlook);
        Add(new Label { Text = "Закрытие Outlook принудительное: возможна потеря несохранённых данных.\nОбрабатываются только процессы с совпадающими SID и ID выбранного сеанса.", AutoSize = true });
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(execute); buttons.Controls.Add(cancel); Add(buttons);
        CancelButton = cancel;
        void UpdateMode() { backups.Enabled = restore.Checked; execute.Enabled = !checking && (apply.Checked || backups.SelectedItem != null); }
        apply.CheckedChanged += (_,_) => UpdateMode(); restore.CheckedChanged += (_,_) => UpdateMode();
        backups.SelectedIndexChanged += (_,_) => UpdateMode();
        execute.Click += async (_,_) =>
        {
            if (checking) return;
            if (CloseOutlook && MessageBox.Show(this, "Outlook будет принудительно завершён только в выбранном сеансе выбранного пользователя. Несохранённые данные могут быть потеряны. Продолжить?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            checking = true; execute.Enabled = false; cancel.Enabled = false;
            apply.Enabled = restore.Enabled = backups.Enabled = closeOutlook.Enabled = false;
            try
            {
                if (IsRestore)
                {
                    var result = await preview(BackupId);
                    if (result.Code != 0) { MessageBox.Show(this, "Восстановление не начато.\n" + result.Message + "\nКонфликтов: " + result.Conflicts.Length + ". Подробнее — в диагностике.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                }
                DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { checking = false; if (!IsDisposed) { cancel.Enabled = true; apply.Enabled = restore.Enabled = closeOutlook.Enabled = true; UpdateMode(); } }
        };
        FormClosing += (_, e) => { if (checking && DialogResult != DialogResult.OK) e.Cancel = true; };
        UpdateMode();
    }
    public static void ShowReport(IWin32Window owner, string title, string report)
    {
        using var form = new Form { Text = title, ClientSize = new(800, 560), StartPosition = FormStartPosition.CenterParent, AutoScaleMode = AutoScaleMode.Dpi };
        var text = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Text = report };
        var copy = new Button { Text = "Копировать отчёт", AutoSize = true, Dock = DockStyle.Bottom };
        copy.Click += (_,_) => { try { Clipboard.SetText(text.Text); } catch (Exception ex) { MessageBox.Show(form, ex.Message); } };
        form.Controls.Add(text); form.Controls.Add(copy); form.ShowDialog(owner);
    }
}
