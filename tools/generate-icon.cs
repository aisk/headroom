#:package System.Drawing.Common@10.0.0
#:property TargetFramework=net10.0-windows

// Generates src/Headroom/Assets/AppIcon.ico in a Fluent-like style: a small guitar amp whose
// grille shows three level bars (the quota "headroom" left).
// Usage: dotnet run tools/generate-icon.cs [output.ico]

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

var output = args.Length > 0 ? args[0] : Path.Combine("src", "Headroom", "Assets", "AppIcon.ico");
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

var images = sizes.Select(size => (size, png: RenderPng(size))).ToList();
using (var stream = File.Create(output))
using (var writer = new BinaryWriter(stream))
{
    // ICONDIR header, then one ICONDIRENTRY per image; every image is stored as PNG.
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)images.Count);
    var offset = 6 + 16 * images.Count;
    foreach (var (size, png) in images)
    {
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(png.Length);
        writer.Write(offset);
        offset += png.Length;
    }

    foreach (var (_, png) in images)
    {
        writer.Write(png);
    }
}

Console.WriteLine($"Wrote {output} ({string.Join(", ", sizes)})");

static byte[] RenderPng(int size)
{
    using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bitmap))
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        var small = size <= 24;
        var s = (float)size;

        // Amp body: wider than tall, sitting slightly low like Fluent objects.
        var body = small
            ? new RectangleF(0.5f, s * 0.12f, s - 1f, s * 0.80f)
            : new RectangleF(s * 0.06f, s * 0.20f, s * 0.88f, s * 0.68f);
        var radius = s * (small ? 0.14f : 0.08f);

        if (!small)
        {
            // Carrying handle on top: the most recognizable amp detail.
            var handle = new RectangleF(s * 0.32f, s * 0.09f, s * 0.36f, s * 0.16f);
            using var handlePen = new Pen(Color.FromArgb(0x6E, 0x72, 0x7C), s * 0.055f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var handlePath = RoundedRect(handle, s * 0.05f);
            g.SetClip(new RectangleF(0, 0, s, body.Y + s * 0.01f));
            g.DrawPath(handlePen, handlePath);
            g.ResetClip();
        }

        if (!small)
        {
            // Soft contact shadow below the object.
            for (var i = 6; i >= 1; i--)
            {
                var spread = s * 0.012f * i;
                var shadowRect = new RectangleF(body.X + spread, body.Bottom - s * 0.04f, body.Width - 2 * spread, s * 0.08f);
                using var shadow = new SolidBrush(Color.FromArgb(10, 0, 0, 0));
                g.FillEllipse(shadow, shadowRect);
            }
        }

        using (var bodyPath = RoundedRect(body, radius))
        using (var bodyBrush = new LinearGradientBrush(body, Color.FromArgb(0x4A, 0x4D, 0x55), Color.FromArgb(0x22, 0x24, 0x29), 90f))
        {
            g.FillPath(bodyBrush, bodyPath);
        }

        if (size >= 32)
        {
            // A subtle light outline keeps the dark body readable on dark backgrounds.
            using var outline = new Pen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), MathF.Max(1f, s * 0.008f));
            using var edge = RoundedRect(new RectangleF(body.X + 0.5f, body.Y + 0.5f, body.Width - 1f, body.Height - 1f), radius);
            g.DrawPath(outline, edge);
        }

        var pad = small ? s * 0.11f : s * 0.07f;
        var grilleTop = body.Y + (small ? pad : body.Height * 0.30f);

        if (!small)
        {
            // Control panel with three knobs.
            var panel = new RectangleF(body.X + pad, body.Y + pad * 0.8f, body.Width - 2 * pad, grilleTop - body.Y - pad * 1.5f);
            using (var panelPath = RoundedRect(panel, panel.Height * 0.3f))
            using (var panelBrush = new LinearGradientBrush(panel, Color.FromArgb(0xF3, 0xF1, 0xEC), Color.FromArgb(0xC9, 0xC6, 0xBE), 90f))
            {
                g.FillPath(panelBrush, panelPath);
            }

            // Evenly spaced knobs with pointer ticks, so they read as amp controls rather than window buttons.
            var knobCount = size >= 48 ? 4 : 3;
            var knob = panel.Height * 0.66f;
            var spacing = panel.Width / knobCount;
            for (var i = 0; i < knobCount; i++)
            {
                var cx = panel.X + spacing * (i + 0.5f);
                var cy = panel.Y + panel.Height / 2;
                var knobRect = new RectangleF(cx - knob / 2, cy - knob / 2, knob, knob);
                using var knobBrush = new LinearGradientBrush(knobRect, Color.FromArgb(0x3A, 0x3C, 0x42), Color.FromArgb(0x16, 0x17, 0x1A), 90f);
                g.FillEllipse(knobBrush, knobRect);
                if (size >= 48)
                {
                    var angle = (-140 + i * 60) * MathF.PI / 180;
                    using var tick = new Pen(Color.White, MathF.Max(1f, knob * 0.14f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.DrawLine(tick, cx, cy, cx + MathF.Cos(angle) * knob * 0.36f, cy + MathF.Sin(angle) * knob * 0.36f);
                }
            }
        }

        // Grille: Windows-blue gradient holding the level bars.
        var grille = new RectangleF(body.X + pad, grilleTop, body.Width - 2 * pad, body.Bottom - pad - grilleTop);
        using (var grillePath = RoundedRect(grille, radius * 0.55f))
        using (var grilleBrush = new LinearGradientBrush(grille, Color.FromArgb(0x29, 0x98, 0xF0), Color.FromArgb(0x0F, 0x5F, 0xC2), 90f))
        {
            g.FillPath(grilleBrush, grillePath);
        }

        // Three level bars: full, two thirds, one third. The empty part is the headroom.
        float[] levels = [1.0f, 0.66f, 0.36f];
        var inner = small ? grille.Width * 0.14f : grille.Width * 0.12f;
        var barHeight = MathF.Max(2f, grille.Height * (small ? 0.19f : 0.15f));
        var gap = MathF.Max(1f, grille.Height * (small ? 0.12f : 0.12f));
        var total = 3 * barHeight + 2 * gap;
        var top = grille.Y + (grille.Height - total) / 2;
        var width = grille.Width - 2 * inner;
        using var track = new SolidBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));
        using var fill = new SolidBrush(Color.White);
        for (var i = 0; i < levels.Length; i++)
        {
            var y = top + i * (barHeight + gap);
            using (var trackPath = RoundedRect(new RectangleF(grille.X + inner, y, width, barHeight), barHeight / 2))
            {
                g.FillPath(track, trackPath);
            }

            using var fillPath = RoundedRect(new RectangleF(grille.X + inner, y, MathF.Max(barHeight, width * levels[i]), barHeight), barHeight / 2);
            g.FillPath(fill, fillPath);
        }
    }

    using var memory = new MemoryStream();
    bitmap.Save(memory, ImageFormat.Png);
    return memory.ToArray();
}

static GraphicsPath RoundedRect(RectangleF rect, float radius)
{
    var diameter = MathF.Min(radius * 2, MathF.Min(rect.Width, rect.Height));
    var path = new GraphicsPath();
    if (diameter <= 0)
    {
        path.AddRectangle(rect);
        return path;
    }

    path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
    path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
    path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
    path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
    path.CloseFigure();
    return path;
}
