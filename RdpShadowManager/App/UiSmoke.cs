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
            form.Show(); Application.DoEvents();
            var grid = Descendants(form).OfType<DataGridView>().Single();
            var picture = Descendants(form).OfType<PictureBox>().Single();
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
        using var executableIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        if (executableIcon == null) throw new InvalidOperationException("EXE icon missing");
        checks.Add("PASS: EXE icon and embedded graphics load from published executable");
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
