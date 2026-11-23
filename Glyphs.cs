using System.Drawing.Text;

namespace ImbotechConsulting.PiExplorer;

/// <summary>Icons from the built-in Windows icon font (Segoe Fluent Icons / Segoe MDL2 Assets).</summary>
internal static class Glyphs
{
    private static readonly string Family =
        FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    public static Font MakeFont(float pt) => new(Family, pt, FontStyle.Regular, GraphicsUnit.Point);

    public static Bitmap MakeBitmap(string glyph, Color color, int px = 20)
    {
        var bmp = new Bitmap(px, px);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using var font = new Font(Family, px * 0.7f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(glyph, font, brush, new RectangleF(0, 0, px, px), fmt);
        return bmp;
    }

    public const string Terminal = ""; // Segoe MDL2 "CommandPrompt"

    public const string Preview = ""; // Segoe MDL2 "View"

    public const string Add ="", Close = "", Copy = "", Move = "", NewFolder = "",
        Delete = "", NewWindow = "", Hidden = "", Pin = "", Star = "";
}
