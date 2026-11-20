using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Globalization;
using PeripheralBatteryTray.Core;

namespace PeripheralBatteryTray.App;

internal static class TrayIconRenderer
{
    internal static readonly Color MouseNumberColor = Color.FromArgb(82, 183, 255);
    internal static readonly Color BluetoothNumberColor = Color.FromArgb(64, 210, 147);
    internal static readonly Color LowNumberColor = Color.FromArgb(255, 89, 89);

    internal static string NumberText(TrayVisual visual) => visual.MinimumPercent is >= 0 and <= 100
        ? visual.MinimumPercent.Value.ToString(CultureInfo.InvariantCulture) : visual.HasOffline ? "—" : "?";

    public static Icon Create(TrayVisual visual, int size, TrayIconStyle style = TrayIconStyle.Battery, Color? numberColor = null)
    {
        if (size is < 16 or > 64) throw new ArgumentOutOfRangeException(nameof(size));
        if (!Enum.IsDefined(style)) throw new ArgumentOutOfRangeException(nameof(style));
        using var bitmap = new Bitmap(size, size);
        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (style == TrayIconStyle.Number) DrawNumber(g, visual, size, numberColor ?? Color.White);
        else DrawBattery(g, visual, size);
        nint handle = bitmap.GetHicon();
        try
        {
            using Icon borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    private static void DrawNumber(Graphics g, TrayVisual visual, int size, Color color)
    {
        string value = NumberText(visual);
        bool numeric = visual.MinimumPercent is >= 0 and <= 100;
        Color fill = !numeric ? Color.FromArgb(170, 170, 170)
            : visual.MinimumPercent <= 20 ? LowNumberColor : color;
        using var path = new GraphicsPath();
        using var font = new FontFamily("Segoe UI");
        path.AddString(value, font, (int)FontStyle.Bold, 32, PointF.Empty, StringFormat.GenericTypographic);
        RectangleF bounds = path.GetBounds();
        float margin = Math.Max(1, size / 24f);
        float height = value == "—" ? Math.Max(1.5f, size / 8f) : size - 2 * margin;
        float width = value == "—" ? size - 4 * margin
            : Math.Min(size - 2 * margin, bounds.Width * height / bounds.Height);
        // Keep three digits full-height; horizontal compression lets 100 remain readable at 16px.
        using var matrix = new Matrix();
        matrix.Translate(-bounds.X, -bounds.Y, MatrixOrder.Append);
        matrix.Scale(width / bounds.Width, height / bounds.Height, MatrixOrder.Append);
        matrix.Translate((size - width) / 2, (size - height) / 2, MatrixOrder.Append);
        path.Transform(matrix);
        using var outline = new Pen(Color.FromArgb(220, 35, 35, 35), Math.Max(1, size / 24f)) { LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(fill);
        g.DrawPath(outline, path);
        g.FillPath(brush, path);
    }

    private static void DrawBattery(Graphics g, TrayVisual visual, int size)
    {
        g.ScaleTransform(size / 32f, size / 32f);
        Color color = visual.MinimumPercent is null ? Color.Gray
            : visual.MinimumPercent <= 20 ? Color.FromArgb(221, 76, 65) : Color.FromArgb(32, 153, 119);
        using var body = new SolidBrush(color);
        using var border = new Pen(Color.White, 2);
        g.FillRectangle(body, 3, 6, 24, 20);
        g.DrawRectangle(border, 3, 6, 24, 20);
        g.FillRectangle(Brushes.White, 28, 12, 2, 8);
        string value = visual.MinimumPercent?.ToString() ?? "?";
        using var font = new Font("Segoe UI", value.Length == 3 ? 10 : 12, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(value, font, Brushes.White, new RectangleF(3, 6, 24, 20), format);
        if (visual.HasOffline || visual.HasUnknown)
        {
            g.FillEllipse(Brushes.DimGray, 22, 21, 9, 9);
            g.DrawEllipse(Pens.White, 22, 21, 9, 9);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
