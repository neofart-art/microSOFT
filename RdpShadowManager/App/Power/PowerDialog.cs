using RdpShadowManager.Core;

namespace RdpShadowManager;
internal sealed class PowerDialog : Form
{
    private readonly PowerTarget target;
    private readonly PowerAction action;
    private readonly NumericUpDown delay = new() { Minimum = 0, Maximum = PowerRequest.MaxDelay, Value = 60, Width = 130 };
    private readonly TextBox comment = new() { MaxLength = 512, Multiline = true, Height = 80, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    private readonly CheckBox force = new() { Text = "Принудительно закрыть приложения", Checked = false, AutoSize = true };
    internal int DelaySeconds => (int)delay.Value;
    internal bool Force => force.Checked;
    public PowerRequest Request => new(target, action, action == PowerAction.Abort ? 0 : DelaySeconds, action == PowerAction.Abort ? "" : comment.Text, action != PowerAction.Abort && Force);
    public PowerDialog(PowerTarget target, PowerAction action)
    {
        this.target = target; this.action = action;
        Text = "Подтверждение действия питания"; AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new(640, action == PowerAction.Abort ? 300 : 510); MinimumSize = new(580, action == PowerAction.Abort ? 300 : 500);
        StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true, Padding = new(12) };
        Controls.Add(layout);
        void Add(Control c) { c.Margin = new(3, 7, 3, 7); layout.Controls.Add(c); }
        var verb = action switch { PowerAction.Restart => "Перезагрузить компьютер", PowerAction.Shutdown => "Выключить компьютер", PowerAction.Abort => "Отменить запланированное действие", _ => throw new ArgumentException("Неизвестное действие.") };
        Add(new Label { Text = $"Компьютер: {target.Computer}\nIP-адрес: {target.Address}\nДействие: {verb}", AutoSize = true, MaximumSize = new(580, 0) });
        Add(new Label { Text = "Действие затронет весь компьютер и всех работающих на нём пользователей.\nЦель зафиксирована; запрос будет отправлен на указанный IP-адрес.", AutoSize = true, MaximumSize = new(580, 0) });
        if (action != PowerAction.Abort)
        {
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            row.Controls.Add(new Label { Text = "Задержка до выполнения, с:", AutoSize = true, Margin = new(3,7,3,3) }); row.Controls.Add(delay); Add(row);
            Add(new Label { Text = "Комментарий для пользователей (до 512 символов):", AutoSize = true }); Add(comment);
            Add(force);
            Add(new Label { Text = "Принудительное закрытие может привести к потере несохранённых данных.\nЕсли флажок выключен, приложения могут задержать или заблокировать завершение работы. При задержке 0 секунд отмена через API невозможна.", AutoSize = true, MaximumSize = new(580, 0) });
        }
        else Add(new Label { Text = "Отмена относится к любому ещё отменяемому выключению или перезагрузке на этой цели, в том числе запланированному другим администратором.", AutoSize = true, MaximumSize = new(580, 0) });
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var execute = new Button { Text = "Выполнить", AutoSize = true };
        var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
        execute.Click += (_,_) =>
        {
            if (action != PowerAction.Abort)
            {
                try { Request.Build(); }
                catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            }
            DialogResult = DialogResult.OK; Close();
        };
        buttons.Controls.Add(execute); buttons.Controls.Add(cancel); Add(buttons); CancelButton = cancel;
        // Enter does not implicitly execute a potentially destructive operation.
    }
}
