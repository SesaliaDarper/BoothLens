using System.Drawing.Drawing2D;

namespace BoothLens.Launcher;

internal sealed class DesktopBarRenderer(Color background, Color foreground, Color selected, Color border) : ToolStripProfessionalRenderer(new Palette(background, selected, border))
{
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(background); e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(border);
        if (e.ToolStrip is MenuStrip) e.Graphics.DrawLine(pen, 0, e.ToolStrip.Height - 1, e.ToolStrip.Width, e.ToolStrip.Height - 1);
        else e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected && !(e.Item is ToolStripMenuItem { Pressed: true })) return;
        var rect = new Rectangle(2, 2, Math.Max(1, e.Item.Width - 4), Math.Max(1, e.Item.Height - 4));
        using var shape = new GraphicsPath(); const int radius = 10;
        shape.AddArc(rect.Left, rect.Top, radius, radius, 180, 90); shape.AddArc(rect.Right - radius, rect.Top, radius, radius, 270, 90);
        shape.AddArc(rect.Right - radius, rect.Bottom - radius, radius, radius, 0, 90); shape.AddArc(rect.Left, rect.Bottom - radius, radius, radius, 90, 90); shape.CloseFigure();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using var brush = new SolidBrush(selected); e.Graphics.FillPath(brush, shape);
    }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? foreground : Color.FromArgb(135, 140, 162); base.OnRenderItemText(e);
    }
    sealed class Palette(Color background, Color selected, Color border) : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => background;
        public override Color ImageMarginGradientBegin => background;
        public override Color ImageMarginGradientMiddle => background;
        public override Color ImageMarginGradientEnd => background;
        public override Color MenuBorder => border;
        public override Color MenuItemBorder => selected;
        public override Color SeparatorDark => border;
        public override Color SeparatorLight => background;
    }
}
