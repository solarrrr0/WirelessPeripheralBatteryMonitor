using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using PeripheralBatteryTray.Core;

namespace PeripheralBatteryTray.App;

internal static class TrayIconRenderer
{
    public static Icon Create(TrayVisual visual, int size)
    {
        using var bitmap = new Bitmap(size, size);
        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
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
        nint handle = bitmap.GetHicon();
        try
        {
            using Icon borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
