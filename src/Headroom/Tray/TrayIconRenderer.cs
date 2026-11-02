using System.Runtime.InteropServices;
using Microsoft.Win32;
using static Headroom.Interop.NativeMethods;

namespace Headroom.Tray;

/// <summary>
/// Draws the tray icon at runtime: one horizontal bar per provider, filled with the remaining quota
/// of its most constrained window. Monochrome to match the other system tray icons.
/// </summary>
internal static class TrayIconRenderer
{
    private const double LowThreshold = 10;

    /// <param name="remainingPercents">Remaining percent per provider; null means no data.</param>
    public static IntPtr Render(IReadOnlyList<double?> remainingPercents)
    {
        var dpi = GetDpiForSystem();
        var size = GetSystemMetricsForDpi(SM_CXSMICON, dpi);
        if (size <= 0)
        {
            size = 16;
        }

        var light = IsTaskbarLight();
        (byte R, byte G, byte B) foreground = light ? ((byte)0x1A, (byte)0x1A, (byte)0x1A) : ((byte)0xFF, (byte)0xFF, (byte)0xFF);
        (byte R, byte G, byte B) critical = light ? ((byte)0xC4, (byte)0x2B, (byte)0x1C) : ((byte)0xFF, (byte)0x99, (byte)0xA4);

        var pixels = new uint[size * size];
        double?[] bars = remainingPercents.Count == 0 ? [null] : [.. remainingPercents.Take(3)];
        var count = bars.Length;
        double barHeight = count switch { 1 => size * 0.34, 2 => size * 0.27, _ => size * 0.2 };
        var gap = barHeight * (count == 2 ? 0.75 : 0.6);
        var total = count * barHeight + (count - 1) * gap;
        var top = (size - total) / 2;
        var left = size * 0.06;
        var right = size - left;

        for (var i = 0; i < count; i++)
        {
            var y0 = top + i * (barHeight + gap);
            var y1 = y0 + barHeight;
            var remaining = bars[i];

            // Track.
            FillBar(pixels, size, left, y0, right, y1, double.PositiveInfinity, foreground, remaining is null ? 0.3 : 0.38);
            if (remaining is not { } value || value <= 0)
            {
                continue;
            }

            // Fill proportional to remaining quota; keep at least a visible dot.
            var clip = left + (right - left) * Math.Clamp(value / 100, 0, 1);
            clip = Math.Max(clip, left + barHeight * 0.9);
            FillBar(pixels, size, left, y0, right, y1, clip, value <= LowThreshold ? critical : foreground, 1);
        }

        return CreateIcon(pixels, size);
    }

    private static void FillBar(uint[] pixels, int size, double x0, double y0, double x1, double y1, double clipX,
        (byte R, byte G, byte B) color, double opacity)
    {
        var radius = (y1 - y0) / 2;
        for (var py = (int)Math.Floor(y0); py < Math.Ceiling(y1) && py < size; py++)
        {
            for (var px = (int)Math.Floor(x0); px < Math.Ceiling(Math.Min(x1, clipX)) && px < size; px++)
            {
                var cx = px + 0.5;
                var cy = py + 0.5;
                // Signed distance to a rounded rectangle gives a cheap anti-aliased edge.
                var nearestX = Math.Clamp(cx, x0 + radius, x1 - radius);
                var nearestY = Math.Clamp(cy, y0 + radius, y1 - radius);
                var distance = Math.Sqrt((cx - nearestX) * (cx - nearestX) + (cy - nearestY) * (cy - nearestY)) - radius;
                var coverage = Math.Clamp(0.5 - distance, 0, 1) * Math.Clamp(clipX - px, 0, 1);
                if (coverage > 0)
                {
                    Blend(ref pixels[py * size + px], color, coverage * opacity);
                }
            }
        }
    }

    private static void Blend(ref uint pixel, (byte R, byte G, byte B) color, double alpha)
    {
        var dstA = (pixel >> 24) / 255.0;
        var outA = alpha + dstA * (1 - alpha);
        if (outA <= 0)
        {
            return;
        }

        byte Mix(byte src, uint dst) => (byte)Math.Round((src * alpha + dst * dstA * (1 - alpha)) / outA);
        var r = Mix(color.R, (pixel >> 16) & 0xFF);
        var g = Mix(color.G, (pixel >> 8) & 0xFF);
        var b = Mix(color.B, pixel & 0xFF);
        pixel = ((uint)Math.Round(outA * 255) << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    private static IntPtr CreateIcon(uint[] pixels, int size)
    {
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size, // Top-down rows.
            biPlanes = 1,
            biBitCount = 32,
        };
        var color = CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        Marshal.Copy((int[])(object)pixels, 0, bits, pixels.Length);
        var maskStride = (size + 15) / 16 * 2;
        var mask = CreateBitmap(size, size, 1, 1, new byte[maskStride * size]);
        try
        {
            var info = new ICONINFO { fIcon = true, hbmMask = mask, hbmColor = color };
            return CreateIconIndirect(ref info);
        }
        finally
        {
            DeleteObject(color);
            DeleteObject(mask);
        }
    }

    private static bool IsTaskbarLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
