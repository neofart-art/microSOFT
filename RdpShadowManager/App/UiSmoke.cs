using System.Drawing.Imaging;

namespace RdpShadowManager;
// CI-only layout simulation; does not claim to replace real multi-monitor DPI testing.
internal static class UiSmoke
{
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        List<string> checks = [];
        foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            using var form = new MainForm();
            form.MaximumSize = new(10000, 10000);
            // Synthetic fixture: never presented as real remote discovery.
            form.SetInfoSmokeFixture();
            form.Show(); Application.DoEvents();
            var grid = Descendants(form).OfType<DataGridView>().Single();
            var picture = Descendants(form).OfType<PictureBox>().Single();
            var connect = Descendants(form).OfType<Button>().Single(b => b.Text == "Подключиться");
            if (connect.Width < 400 || connect.Height < 46 || !connect.Font.Bold || connect.FlatAppearance.BorderSize != 2)
                throw new InvalidOperationException("Connect button size/style regression");
            var power = Descendants(form).OfType<GroupBox>().Single(g => g.Text == "Питание удалённого компьютера");
            var summary = Descendants(form).OfType<GroupBox>().Single(g => g.Text == "Компьютер и время работы");
            if (power.Height > 150 || summary.Top < power.Bottom) throw new InvalidOperationException("Power block must be compact with summary below it");
            var baseline = grid.Size;
            var baselineClient = form.ClientSize;
            if (scale != 1f)
            {
                form.Scale(new SizeF(scale, scale));
                form.ClientSize = new((int)Math.Round(baselineClient.Width * scale), (int)Math.Round(baselineClient.Height * scale));
            }
            form.PerformLayout(); Application.DoEvents();
            void Check(bool value, string message) { if (!value) throw new InvalidOperationException($"{scale * 100}%: {message}"); }
            Check(form.Icon != null && form.ShowInTaskbar && form.ShowIcon, "Window/taskbar icon missing");
            Check(picture.Image is { Width: 1402, Height: 1122 }, "Full embedded image missing");
            Check(picture.SizeMode == PictureBoxSizeMode.Zoom, "Image aspect ratio not preserved");
            Check(grid.Width >= baseline.Width * scale * 0.94 && grid.Height >= baseline.Height * scale * 0.94, $"Table shrank: baseline {baseline}, scaled {grid.Size}, client {form.ClientSize}");
            var artBounds = form.RectangleToClient(picture.RectangleToScreen(picture.ClientRectangle));
            var gridBounds = form.RectangleToClient(grid.RectangleToScreen(grid.ClientRectangle));
            Check(!artBounds.IntersectsWith(gridBounds), "Mascot overlaps the table");
            foreach (var field in Descendants(form).OfType<TextBox>().Where(t => t.ReadOnly && t.Parent?.Parent?.Parent is GroupBox))
            {
                var bounds = form.RectangleToClient(field.RectangleToScreen(field.ClientRectangle));
                Check(!bounds.IntersectsWith(gridBounds), "PC details overlap table");
                Check(field.Width >= 120 * scale, "PC detail field is too narrow");
                Check(field.ReadOnly && field.ShortcutsEnabled && field.WordWrap, "PC fields must support selection/copy and wrapping");
                if (field is ReadableInfoField readable) Check(field.ScrollBars == ScrollBars.None && field.Height >= readable.RequiredHeight(), "Full PC value must fit without internal scrolling");
            }
            foreach (var button in Descendants(form).OfType<Button>().Where(b => b.Visible))
            {
                var bounds = form.RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
                Check(!artBounds.IntersectsWith(bounds), $"Mascot overlaps {button.Text}");
                Check(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= form.ClientSize.Width && bounds.Bottom <= form.ClientSize.Height, $"Button clipped: {button.Text}");
            }
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, form.ClientRectangle);
            bitmap.Save(Path.Combine(output, $"layout-{scale * 100:0}.png"), ImageFormat.Png);
            checks.Add($"PASS: layout simulation {scale * 100:0}% | grid {grid.Width}x{grid.Height} | mascot {picture.Width}x{picture.Height}");
            form.Close();
        }
        MainForm.CheckAddressHistoryUi(output);
        checks.Add("PASS: address history survives form restart, editable dropdown/autocomplete, selection invalidates old sessions, MRU refresh preserves unchanged target");
        var infoChecks = MainForm.CheckComputerInfoStates();
        while (!infoChecks.IsCompleted) { Application.DoEvents(); Thread.Sleep(10); }
        infoChecks.GetAwaiter().GetResult();
        checks.Add("PASS: server-confirmed RDP IPv4, unrelated 26 address rejected, no IPv6 or DNS guesses, field copy, stale responses, and 0 changed/36 matching/service-stderr current status without a modal");
        var systemChecks = SystemInfoSmoke.Run(output);
        while (!systemChecks.IsCompleted) { Application.DoEvents(); Thread.Sleep(10); }
        systemChecks.GetAwaiter().GetResult();
        checks.Add("PASS: additional PC info categories, readonly copy, partial/unknown states, access denial and single-flight refresh (synthetic fixtures)");
        MainForm.CheckHiddenPowerShell();
        checks.Add("PASS: real hidden Windows PowerShell process reports GetConsoleWindow=0; no console was allocated");
        PowerSmoke.Run(output);
        checks.Add("PASS: power worker local-target protection and confirmation defaults (no real power API calls)");
        using var executableIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        if (executableIcon == null) throw new InvalidOperationException("EXE icon missing");
        checks.Add("PASS: EXE icon and embedded graphics load from published executable");
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var account = identity.Name.Split('\\', 2);
        var request = new MaintenanceRequest("Inspect", "localhost", MaintenanceClient.LocalMachineId(),
            Math.Max(1, System.Diagnostics.Process.GetCurrentProcess().SessionId), account.Last(), account.Length == 2 ? account[0] : Environment.MachineName);
        var transport = Task.Run(() => MaintenanceClient.Run(request)).GetAwaiter().GetResult();
        if (transport.ExitCode == 0) throw new InvalidOperationException("Local maintenance target was not rejected");
        File.WriteAllText(Path.Combine(output, "local-target-rejected.json"), transport.Result.Report());
        checks.Add("PASS: PowerShell transport returns structured nonzero result for local target / unavailable WinRM; no local fallback");
        File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
    }
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}

