using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Security.Principal;
using RdpShadowManager.Core;

namespace RdpShadowManager;
internal sealed partial class MainForm : Form
{
    private readonly TextBox host = new() { Width = 280, MaxLength = 253 };
    private readonly Button refresh = new() { Text = "Получить сеансы", AutoSize = true };
    private readonly Button connect = new() { Text = "Подключиться", AutoSize = true, Enabled = false };
    private readonly RadioButton consent = new() { Text = "С запросом согласия пользователя", Checked = true, AutoSize = true };
    private readonly RadioButton noConsent = new() { Text = "Без запроса согласия пользователя", AutoSize = true };
    private readonly DataGridView grid = new() { Dock = DockStyle.Fill, ReadOnly = true, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    private readonly Label status = new() { Text = "Введите компьютер и получите сеансы.", AutoSize = true, Dock = DockStyle.Fill };
    private readonly ProgressBar progress = new() { Style = ProgressBarStyle.Marquee, Visible = false, Width = 120 };
    private readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill };
    private readonly Panel diagnostics = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly NumericUpDown timeout = new() { Minimum = 3, Maximum = 120, Value = 15, Width = 60 };
    private CancellationTokenSource? request;
    private Task? operation;
    private bool closing;
    private bool busy;
    private bool isElevated;
    private readonly Button elevate = new() { Text = "Перезапустить от имени администратора", AutoSize = true };
    private int revision;
    private Session? Selected => grid.CurrentRow?.DataBoundItem as Session;
    public MainForm(StartupState? restored = null)
    {
        Text = "RDP Shadow Manager"; AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new(950, 840); MinimumSize = new(780, 700);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, Padding = new(12) };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        for (int i=2;i<9;i++) layout.RowStyles.Add(new(SizeType.AutoSize));
        Controls.Add(layout);
        FlowLayoutPanel Row(params Control[] controls)
        {
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            row.Controls.AddRange(controls); return row;
        }
        layout.Controls.Add(Row(new Label { Text = "Компьютер: имя или IP", AutoSize = true, Margin = new(3,8,3,3) }, host, refresh,
            new Label { Text = "Тайм-аут, с", AutoSize = true, Margin = new(3,8,3,3) }, timeout),0,0);
        foreach (var (property, title) in new[] { ("User", "Пользователь"), ("Domain", "Домен"), ("Id", "ID сеанса"), ("Name", "Сеанс"), ("Status", "Состояние") })
            grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = property, HeaderText = title, SortMode = DataGridViewColumnSortMode.NotSortable });
        layout.Controls.Add(grid,0,1); layout.Controls.Add(Row(consent, noConsent),0,2);
        using var identity = WindowsIdentity.GetCurrent();
        bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        isElevated = elevated; elevate.Enabled = !elevated;
        var administration = new GroupBox { Text = "Администрирование", AutoSize = true, Dock = DockStyle.Fill, Padding = new(8), Margin = new(3, 12, 3, 8) };
        administration.Controls.Add(Row(new Label { Text = elevated ? "Программа запущена от имени администратора" : "Программа запущена с обычными правами", AutoSize = true, Margin = new(3,8,12,3) }, elevate));
        layout.Controls.Add(Row(connect, progress),0,3);
        layout.Controls.Add(administration,0,4);
        var maintenance = new GroupBox { Text = "Обслуживание", AutoSize = true, Dock = DockStyle.Fill, Padding = new(8) };
        maintenance.Controls.Add(Row(repair, target));
        layout.Controls.Add(maintenance,0,5); layout.Controls.Add(status,0,6);
        var toggle = new CheckBox { Text = "Диагностика", AutoSize = true };
        layout.Controls.Add(toggle,0,7);
        diagnostics.Height = 150;
        var copy = new Button { Text = "Копировать", AutoSize = true, Dock = DockStyle.Bottom };
        diagnostics.Controls.Add(log); diagnostics.Controls.Add(copy); layout.Controls.Add(diagnostics,0,8);
        toggle.CheckedChanged += (_,_) => { diagnostics.Visible = toggle.Checked; layout.RowStyles[8].SizeType = SizeType.Absolute; layout.RowStyles[8].Height = toggle.Checked ? 150 : 0; };
        copy.Click += (_,_) => { try { if (log.TextLength > 0) Clipboard.SetText(log.Text); } catch (Exception ex) { Failure("Копирование", host.Text, ex); } };
        host.TextChanged += (_,_) => { revision++; request?.Cancel(); grid.DataSource = null; UpdateConnect(); status.Text = "Компьютер изменён. Получите сеансы заново."; };
        grid.SelectionChanged += (_,_) => UpdateConnect();
        refresh.Click += async (_,_) => await Run(false);
        connect.Click += async (_,_) => await Run(true);
        repair.Click += async (_,_) => await Service();
        elevate.Click += (_,_) =>
        {
            if (servicing) return;
            try
            {
                var saved = new StartupState(host.Text, noConsent.Checked);
                // Base64 has no spaces or shell metacharacters; runas uses ShellExecute.
                var start = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = true, Verb = "runas",
                    Arguments = "--resume-state " + saved.Encode()
                };
                using var process = Process.Start(start);
                if (process != null) Close();
            }
            catch (Exception ex) { Failure("Повышение прав", host.Text, ex); }
        };
        FormClosing += (_,e) =>
        {
            if (servicing) { e.Cancel = true; status.Text = "Дождитесь завершения обслуживания и отчёта."; return; }
            if (operation is { IsCompleted: false })
            {
                e.Cancel = true;
                if (!closing) { closing = true; request?.Cancel(); _ = CloseAfterOperation(); }
            }
        };
        AcceptButton = refresh;
        if (restored != null)
        {
            host.Text = restored.Computer;
            noConsent.Checked = restored.NoConsent;
            consent.Checked = !restored.NoConsent;
            Shown += async (_,_) => await Run(false);
        }
    }
    private async Task CloseAfterOperation() { try { await operation!; } finally { Close(); } }
    private void UpdateConnect()
    {
        connect.Enabled = !busy && Selected?.CanConnect == true;
        repair.Enabled = !busy && !serviceUncertain && Selected?.CanConnect == true;
        target.Text = Selected is {} selected ? $"Компьютер: {host.Text}   Пользователь: {selected.Domain}\\{selected.User}   ID сеанса: {selected.Id}\nSID будет проверен через WinRM перед подтверждением." : "Цель: выберите активный сеанс.";
        if (!busy && Selected is { CanConnect: false }) status.Text = "Этот сеанс не активен: подключение недоступно. Обновите список.";
    }
    private void Trace(string operationName, string computer, string detail, int code = 0) =>
        log.AppendText($"{DateTimeOffset.Now:O} | {operationName} | {computer} | код={code} | {detail}{Environment.NewLine}");
    private void Failure(string op, string computer, Exception ex)
    {
        var code = ex is Win32Exception w ? w.NativeErrorCode : ex.HResult;
        status.Text = ex switch
        {
            TimeoutException => "Истёк тайм-аут запроса. Проверьте адрес, RPC и доступность службы.",
            Win32Exception when code == 5 => "Доступ запрещён. Проверьте права на удалённом компьютере.",
            Win32Exception when code is 53 or 1722 => "Удалённый компьютер или служба недоступны. Подробности в диагностике.",
            Win32Exception when code is 11001 or 12007 => "Не удалось разрешить имя компьютера.",
            _ => ex.Message
        };
        Trace(op, computer, ex.Message, code);
    }
    private async Task Run(bool launch)
    {
        if (busy) return;
        string computer;
        try { computer = Connection.ValidateHost(host.Text); }
        catch (Exception ex) { Failure("Проверка адреса", host.Text, ex); return; }
        var selected = Selected;
        if (launch && selected?.CanConnect != true) return;
        var noPrompt = noConsent.Checked;
        int version = revision;
        request = new(); busy = true; refresh.Enabled = false; connect.Enabled = false; progress.Visible = true;
        status.Text = launch ? "Проверка выбранного сеанса…" : "Получение сеансов…";
        var current = Execute(); operation = current;
        try { await current; }
        finally
        {
            request.Dispose(); request = null; busy = false; refresh.Enabled = true; progress.Visible = false; UpdateConnect();
        }
        async Task Execute()
        {
            try
            {
                var result = await Query(computer, TimeSpan.FromSeconds((double)timeout.Value), request.Token);
                if (version != revision || closing) return;
                foreach (var detail in result.Diagnostics) Trace("WTS", computer, detail);
                if (result.Error != null) throw new Win32Exception(result.ErrorCode, result.Error);
                Trace("Получение сеансов", computer, $"Найдено: {result.Sessions.Count}");
                if (!launch)
                {
                    grid.DataSource = result.Sessions; grid.ClearSelection(); grid.CurrentCell = null;
                    status.Text = result.Sessions.Count == 0 ? "Пользовательские сеансы отсутствуют или сведения недоступны. См. диагностику." : "Выберите активный сеанс.";
                    return;
                }
                var actual = result.Sessions.Find(s => s.Id == selected!.Id);
                if (actual == null || !Connection.SameActiveUser(selected!, actual))
                {
                    grid.DataSource = null;
                    throw new InvalidOperationException("Сеанс завершился, изменился или не активен. Получите сеансы и выберите пользователя заново.");
                }
                // An unavailable identity field must never be treated as a matching identity.
                if (string.IsNullOrEmpty(actual.Domain)) throw new InvalidOperationException("Не удалось подтвердить домен пользователя. Обновите список и проверьте права.");
                var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mstsc.exe");
                if (!File.Exists(executable)) throw new FileNotFoundException("mstsc.exe не найден в системном каталоге Windows.");
                var start = new ProcessStartInfo(executable) { UseShellExecute = false };
                foreach (var arg in Connection.Arguments(computer, actual.Id, noPrompt)) start.ArgumentList.Add(arg);
                using var client = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить mstsc.exe.");
                status.Text = "Клиент подключения запущен";
                Trace("Запуск mstsc", computer, string.Join(" ", start.ArgumentList));
            }
            catch (OperationCanceledException) { if (version == revision && !closing) status.Text = "Запрос отменён."; }
            catch (Exception ex) { if (version == revision && !closing) Failure(launch ? "Проверка/запуск подключения" : "Получение сеансов", computer, ex); }
        }
    }
    private static async Task<QueryResult> Query(string computer, TimeSpan timeout, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.ArgumentList.Add("--query"); start.ArgumentList.Add(computer);
        using var worker = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить процесс WTS.");
        var output = worker.StandardOutput.ReadToEndAsync();
        var errors = worker.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        try
        {
            await worker.WaitForExitAsync(deadline.Token);
            if (worker.ExitCode != 0) throw new InvalidOperationException($"Процесс WTS завершён с кодом {worker.ExitCode}: {await errors}");
            return JsonSerializer.Deserialize<QueryResult>(await output) ?? throw new InvalidOperationException("Процесс WTS вернул пустой ответ.");
        }
        catch (OperationCanceledException)
        {
            if (!worker.HasExited) worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync(); // Never allow the next query while this worker is alive.
            await Task.WhenAll(output, errors);
            if (cancellation.IsCancellationRequested) throw;
            throw new TimeoutException("Превышено время ожидания WTS.");
        }
        finally
        {
            if (!worker.HasExited) { worker.Kill(entireProcessTree: true); await worker.WaitForExitAsync(); }
        }
    }
}
