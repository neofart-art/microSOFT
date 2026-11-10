namespace RdpShadowManager;

/// <summary>Owns the full caption/value height instead of relying on native TextBox preferred sizing.</summary>
internal sealed class InfoValueCard : Panel
{
    private readonly Control caption;
    private readonly TextBox value;
    private bool arranging;
    public InfoValueCard(Control caption, TextBox value)
    {
        this.caption = caption; this.value = value;
        Dock = DockStyle.Top; Margin = new(4, 6, 4, 6);
        caption.AutoSize = false; caption.Dock = DockStyle.None; caption.Margin = Padding.Empty;
        value.Dock = DockStyle.None; value.Margin = Padding.Empty;
        Controls.Add(caption); Controls.Add(value);
        value.TextChanged += (_, _) => { PerformLayout(); Parent?.PerformLayout(); };
        caption.TextChanged += (_, _) => { PerformLayout(); Parent?.PerformLayout(); };
    }
    private int Gap => Math.Max(4, (int)Math.Ceiling(4 * DeviceDpi / 96d));
    private int CaptionHeight(int width) => TextRenderer.MeasureText(caption?.Text ?? " ", caption?.Font ?? Font,
        new Size(Math.Max(1, width), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
    private int ContentHeight(int width) => caption == null || value == null ? 24 :
        CaptionHeight(width) + Gap + value.GetPreferredSize(new(Math.Max(40, width), 0)).Height;
    public override Size GetPreferredSize(Size proposedSize)
    {
        int width = proposedSize.Width > 1 && proposedSize.Width < int.MaxValue ? proposedSize.Width : Width;
        return new(Math.Max(40, width), ContentHeight(Math.Max(40, width)));
    }
    protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        => base.SetBoundsCore(x, y, width, ContentHeight(width), specified);
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (arranging || caption == null || value == null) return;
        arranging = true;
        try
        {
            int width = Math.Max(40, ClientSize.Width);
            int top = CaptionHeight(width);
            caption.SetBounds(0, 0, width, top);
            value.SetBounds(0, top + Gap, width, value.GetPreferredSize(new(width, 0)).Height);
            Height = value.Bottom;
        }
        finally { arranging = false; }
    }
}
