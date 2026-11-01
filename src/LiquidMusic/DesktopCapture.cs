using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace LiquidMusic;

/// <summary>Снимок рабочего стола вокруг прямоугольника (физические px) — стекло в шейдере преломляет его.
/// Окна приложения исключены из захвата через SetWindowDisplayAffinity, поэтому снимок «видит сквозь» них.</summary>
internal static class DesktopCapture
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, int operation);

    private static readonly ImageCodecInfo? Jpeg = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public static object? Grab(Rectangle rect, int pad = 56, double scale = 0.6, long quality = 72)
    {
        if (rect.Width < 8 || rect.Height < 8) return null;
        var area = Rectangle.Inflate(rect, pad, pad);
        using var full = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
        // BitBlt напрямую: CopyFromScreen не принимает SourceCopy|CaptureBlt, а CAPTUREBLT нужен, чтобы попали окна-слои
        var screen = GetDC(0);
        try
        {
            using var g = Graphics.FromImage(full);
            var hdc = g.GetHdc();
            try { BitBlt(hdc, 0, 0, area.Width, area.Height, screen, area.Left, area.Top, 0x00CC0020 | 0x40000000); }
            finally { g.ReleaseHdc(hdc); }
        }
        finally { ReleaseDC(0, screen); }
        var w = Math.Max(16, (int)(area.Width * scale)); var h = Math.Max(16, (int)(area.Height * scale));
        using var small = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(full, new Rectangle(0, 0, w, h));
        }
        using var stream = new MemoryStream();
        if (Jpeg != null)
        {
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            small.Save(stream, Jpeg, parameters);
        }
        else small.Save(stream, ImageFormat.Jpeg);
        // w, h, left, top — прямоугольник окна (без поля pad) в физических px экрана
        return new { ok = true, data = "data:image/jpeg;base64," + Convert.ToBase64String(stream.ToArray()), w = rect.Width, h = rect.Height, pad, left = rect.Left, top = rect.Top };
    }

    public static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var d = Math.Max(2, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
