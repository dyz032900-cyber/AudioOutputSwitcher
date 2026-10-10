using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class GenerateSpeakerIcon
{
    private enum IconVariant
    {
        Minimal,
        Orbit,
        Codex
    }

    private static readonly int[] Sizes = new int[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    private static int Main(string[] args)
    {
        string output = Path.Combine("assets", "speaker.ico");
        IconVariant variant = IconVariant.Minimal;
        int pngSize = 256;

        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            if (string.Equals(argument, "--variant", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                variant = ParseVariant(args[++i]);
                continue;
            }

            if (string.Equals(argument, "--size", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                int parsedSize;
                if (int.TryParse(args[++i], out parsedSize) && parsedSize > 0)
                {
                    pngSize = parsedSize;
                }

                continue;
            }

            output = argument;
        }

        string directory = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string extension = Path.GetExtension(output);
        if (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase))
        {
            using (Bitmap bitmap = RenderBitmap(pngSize, variant))
            {
                bitmap.Save(output, ImageFormat.Png);
            }

            Console.WriteLine("Generated " + output + " (" + variant + ")");
            return 0;
        }

        List<byte[]> images = new List<byte[]>();
        for (int i = 0; i < Sizes.Length; i++)
        {
            images.Add(RenderPng(Sizes[i], variant));
        }

        using (FileStream file = File.Create(output))
        using (BinaryWriter writer = new BinaryWriter(file))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)Sizes.Length);

            int offset = 6 + Sizes.Length * 16;
            for (int i = 0; i < Sizes.Length; i++)
            {
                int size = Sizes[i];
                byte[] image = images[i];
                writer.Write((byte)(size == 256 ? 0 : size));
                writer.Write((byte)(size == 256 ? 0 : size));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write((uint)image.Length);
                writer.Write((uint)offset);
                offset += image.Length;
            }

            for (int i = 0; i < images.Count; i++)
            {
                writer.Write(images[i]);
            }
        }

        Console.WriteLine("Generated " + output + " (" + variant + ")");
        return 0;
    }

    private static IconVariant ParseVariant(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return IconVariant.Minimal;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "a":
            case "minimal":
                return IconVariant.Minimal;
            case "b":
            case "orbit":
            case "signal":
                return IconVariant.Orbit;
            case "c":
            case "codex":
            case "relay":
                return IconVariant.Codex;
            default:
                return IconVariant.Minimal;
        }
    }

    private static Bitmap RenderBitmap(int size, IconVariant variant)
    {
        int scale = size <= 24 ? 5 : size <= 48 ? 3 : 2;
        int renderSize = size * scale;

        using (Bitmap renderBitmap = new Bitmap(renderSize, renderSize, PixelFormat.Format32bppArgb))
        using (Graphics graphics = Graphics.FromImage(renderBitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;

            if (size <= 24)
            {
                DrawCompactVariant(graphics, renderSize, variant);
            }
            else
            {
                DrawFullVariant(graphics, renderSize, variant);
            }

            Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics downsample = Graphics.FromImage(bitmap))
            {
                downsample.Clear(Color.Transparent);
                downsample.SmoothingMode = SmoothingMode.HighQuality;
                downsample.InterpolationMode = InterpolationMode.HighQualityBicubic;
                downsample.PixelOffsetMode = PixelOffsetMode.HighQuality;
                downsample.CompositingQuality = CompositingQuality.HighQuality;
                downsample.DrawImage(renderBitmap, new Rectangle(0, 0, size, size), new Rectangle(0, 0, renderSize, renderSize), GraphicsUnit.Pixel);
            }

            return bitmap;
        }
    }

    private static byte[] RenderPng(int size, IconVariant variant)
    {
        using (Bitmap bitmap = RenderBitmap(size, variant))
        using (MemoryStream stream = new MemoryStream())
        {
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
    }

    private static void DrawCompactVariant(Graphics graphics, int size, IconVariant variant)
    {
        DrawTile(graphics, size, size <= 16 ? 0.85f : 0.95f, Math.Max(3.4f, size * 0.24f), variant);
        switch (variant)
        {
            case IconVariant.Orbit:
                DrawCompactOrbit(graphics, size);
                break;
            case IconVariant.Codex:
                DrawCompactCodex(graphics, size);
                break;
            default:
                DrawCompactMinimal(graphics, size);
                break;
        }
    }

    private static void DrawFullVariant(Graphics graphics, int size, IconVariant variant)
    {
        DrawTile(graphics, size, size * 0.07f, Math.Max(8f, size * 0.19f), variant);
        switch (variant)
        {
            case IconVariant.Orbit:
                DrawFullOrbit(graphics, size);
                break;
            case IconVariant.Codex:
                DrawFullCodex(graphics, size);
                break;
            default:
                DrawFullMinimal(graphics, size);
                break;
        }
    }

    private static void DrawTile(Graphics graphics, int size, float inset, float radius, IconVariant variant)
    {
        RectangleF tileRect = new RectangleF(inset, inset, size - inset * 2f, size - inset * 2f);
        DrawTileShadow(graphics, tileRect, radius, size);

        using (GraphicsPath tile = CreateRoundedRectangle(tileRect, radius))
        using (LinearGradientBrush fillBrush = new LinearGradientBrush(
            new PointF(tileRect.Left, tileRect.Top),
            new PointF(tileRect.Right, tileRect.Bottom),
            Color.FromArgb(255, 21, 24, 31),
            Color.FromArgb(255, 30, 34, 42)))
        using (Pen borderPen = new Pen(Color.FromArgb(78, 202, 209, 222), Math.Max(0.85f, size * 0.008f)))
        {
            graphics.FillPath(fillBrush, tile);
            graphics.DrawPath(borderPen, tile);
        }

        if (variant == IconVariant.Orbit && size >= 24)
        {
            using (Pen accentPen = new Pen(Color.FromArgb(110, 99, 170, 255), Math.Max(1.0f, size * 0.018f)))
            {
                accentPen.StartCap = LineCap.Round;
                accentPen.EndCap = LineCap.Round;
                graphics.DrawArc(accentPen, tileRect.Left + size * 0.09f, tileRect.Top + size * 0.09f, tileRect.Width - size * 0.18f, tileRect.Height - size * 0.18f, 212, 74);
            }
        }
    }

    private static void DrawCompactMinimal(Graphics graphics, int size)
    {
        DrawSpeakerGlyph(graphics, size * 0.25f, size * 0.34f, Math.Max(2.5f, size * 0.15f), Math.Max(4.2f, size * 0.28f), Math.Max(4.6f, size * 0.24f), Math.Max(6.4f, size * 0.40f), false);
        using (Pen wavePen = CreateRoundPen(Color.FromArgb(255, 110, 181, 255), size <= 16 ? 1.25f : 1.55f))
        {
            graphics.DrawArc(wavePen, size * 0.58f, size * 0.34f, size * 0.18f, size * 0.34f, -52, 104);
        }
    }

    private static void DrawCompactOrbit(Graphics graphics, int size)
    {
        DrawSpeakerGlyph(graphics, size * 0.24f, size * 0.34f, Math.Max(2.8f, size * 0.15f), Math.Max(5.2f, size * 0.28f), Math.Max(5.2f, size * 0.24f), Math.Max(7.6f, size * 0.42f), false);

        RectangleF orbitRect = new RectangleF(size * 0.16f, size * 0.16f, size * 0.68f, size * 0.68f);
        float ringWidth = size <= 80 ? 3.8f : size * 0.034f;
        DrawOrbitRing(graphics, orbitRect, ringWidth, true);

        using (Pen wavePen = CreateRoundPen(Color.FromArgb(255, 46, 133, 255), size <= 80 ? 3.6f : size * 0.033f))
        {
            graphics.DrawArc(wavePen, size * 0.57f, size * 0.34f, size * 0.14f, size * 0.28f, -52, 104);
        }

        DrawOrbitNode(graphics, new PointF(size * 0.30f, size * 0.29f), size * 0.050f, false, true);
        DrawOrbitNode(graphics, new PointF(size * 0.31f, size * 0.71f), size * 0.050f, false, true);
        DrawOrbitNode(graphics, new PointF(size * 0.77f, size * 0.50f), size * 0.056f, true, true);
    }

    private static void DrawCompactCodex(Graphics graphics, int size)
    {
        DrawSpeakerGlyph(graphics, size * 0.24f, size * 0.34f, Math.Max(2.8f, size * 0.15f), Math.Max(5.0f, size * 0.28f), Math.Max(4.8f, size * 0.23f), Math.Max(7.0f, size * 0.40f), false);

        PointF hub = new PointF(size * 0.57f, size * 0.50f);
        PointF topNode = new PointF(size * 0.75f, size * 0.36f);
        PointF bottomNode = new PointF(size * 0.75f, size * 0.64f);

        float routeWidth = size <= 80 ? 4.4f : size * 0.040f;
        DrawTechCurve(graphics, hub, topNode, new PointF(size * 0.67f, size * 0.48f), new PointF(size * 0.70f, size * 0.42f), routeWidth);
        DrawTechCurve(graphics, hub, bottomNode, new PointF(size * 0.67f, size * 0.52f), new PointF(size * 0.70f, size * 0.58f), routeWidth);

        DrawCompactNode(graphics, hub, size * 0.060f, true);
        DrawCompactNode(graphics, topNode, size * 0.064f, false);
        DrawCompactNode(graphics, bottomNode, size * 0.064f, false);
    }

    private static void DrawFullMinimal(Graphics graphics, int size)
    {
        DrawSpeakerGlyph(graphics, size * 0.27f, size * 0.35f, size * 0.14f, size * 0.26f, size * 0.18f, size * 0.40f, true);

        using (Pen wavePen = CreateRoundPen(Color.FromArgb(255, 104, 179, 255), Math.Max(2.6f, size * 0.056f)))
        {
            graphics.DrawArc(wavePen, size * 0.57f, size * 0.31f, size * 0.19f, size * 0.39f, -52, 104);
        }
    }

    private static void DrawFullOrbit(Graphics graphics, int size)
    {
        DrawSpeakerGlyph(graphics, size * 0.26f, size * 0.35f, size * 0.115f, size * 0.16f, size * 0.15f, size * 0.30f, true);

        RectangleF orbitRect = new RectangleF(size * 0.17f, size * 0.17f, size * 0.66f, size * 0.66f);
        float ringWidth = Math.Max(7.0f, size * 0.028f);
        DrawOrbitRing(graphics, orbitRect, ringWidth, false);

        using (Pen wavePen = CreateRoundPen(Color.FromArgb(255, 42, 129, 255), Math.Max(6.5f, size * 0.026f)))
        {
            graphics.DrawArc(wavePen, size * 0.58f, size * 0.34f, size * 0.13f, size * 0.28f, -52, 104);
        }

        DrawOrbitNode(graphics, new PointF(size * 0.31f, size * 0.29f), size * 0.041f, false, false);
        DrawOrbitNode(graphics, new PointF(size * 0.31f, size * 0.71f), size * 0.041f, false, false);
        DrawOrbitNode(graphics, new PointF(size * 0.77f, size * 0.50f), size * 0.046f, true, false);
    }

    private static void DrawFullCodex(Graphics graphics, int size)
    {
        DrawCodexFrame(graphics, size);
        DrawSpeakerGlyph(graphics, size * 0.25f, size * 0.40f, size * 0.11f, size * 0.16f, size * 0.12f, size * 0.26f, true);

        PointF hub = new PointF(size * 0.55f, size * 0.50f);
        PointF topNode = new PointF(size * 0.74f, size * 0.35f);
        PointF bottomNode = new PointF(size * 0.74f, size * 0.67f);

        float routeWidth = Math.Max(8.0f, size * 0.032f);
        DrawTechCurve(graphics, hub, topNode, new PointF(size * 0.65f, size * 0.49f), new PointF(size * 0.67f, size * 0.43f), routeWidth);
        DrawTechCurve(graphics, hub, bottomNode, new PointF(size * 0.65f, size * 0.51f), new PointF(size * 0.67f, size * 0.58f), routeWidth);

        DrawRingNode(graphics, hub, size * 0.039f, true);
        DrawRingNode(graphics, topNode, size * 0.045f, false);
        DrawRingNode(graphics, bottomNode, size * 0.045f, false);
    }

    private static void DrawSpeakerGlyph(Graphics graphics, float left, float top, float bodyWidth, float bodyHeight, float hornWidth, float hornHeight, bool useGradient)
    {
        float shadowOffset = Math.Max(1.2f, bodyWidth * 0.10f);
        using (GraphicsPath shadow = CreateSpeakerPath(left + shadowOffset, top + shadowOffset, bodyWidth, bodyHeight, hornWidth, hornHeight))
        using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(useGradient ? 78 : 42, 0, 0, 0)))
        {
            graphics.FillPath(shadowBrush, shadow);
        }

        using (GraphicsPath speaker = CreateSpeakerPath(left, top, bodyWidth, bodyHeight, hornWidth, hornHeight))
        {
            if (useGradient)
            {
                using (LinearGradientBrush brush = new LinearGradientBrush(
                    new PointF(left, top),
                    new PointF(left + bodyWidth + hornWidth, top + hornHeight),
                    Color.FromArgb(255, 240, 242, 247),
                    Color.FromArgb(255, 150, 156, 170)))
                {
                    graphics.FillPath(brush, speaker);
                }

                using (Pen outline = new Pen(Color.FromArgb(116, 236, 240, 247), Math.Max(1.2f, bodyWidth * 0.085f)))
                {
                    graphics.DrawPath(outline, speaker);
                }
            }
            else
            {
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(246, 249, 252)))
                {
                    graphics.FillPath(brush, speaker);
                }

                using (Pen outline = new Pen(Color.FromArgb(88, 232, 238, 244), Math.Max(0.9f, bodyWidth * 0.08f)))
                {
                    graphics.DrawPath(outline, speaker);
                }
            }
        }
    }

    private static GraphicsPath CreateSpeakerPath(float left, float top, float bodyWidth, float bodyHeight, float hornWidth, float hornHeight)
    {
        GraphicsPath speaker = new GraphicsPath();
        speaker.AddRoundedRectangle(new RectangleF(left, top, bodyWidth, bodyHeight), Math.Max(1.1f, bodyWidth * 0.42f));
        speaker.StartFigure();
        speaker.AddPolygon(new PointF[]
        {
            new PointF(left + bodyWidth, top),
            new PointF(left + bodyWidth + hornWidth, top - (hornHeight - bodyHeight) * 0.5f),
            new PointF(left + bodyWidth + hornWidth, top + bodyHeight + (hornHeight - bodyHeight) * 0.5f),
            new PointF(left + bodyWidth, top + bodyHeight)
        });
        return speaker;
    }

    private static void DrawTileShadow(Graphics graphics, RectangleF rect, float radius, int size)
    {
        for (int i = 0; i < 4; i++)
        {
            float spread = size * (0.008f + i * 0.004f);
            RectangleF shadowRect = new RectangleF(rect.Left + size * 0.004f - spread * 0.25f, rect.Top + size * 0.016f, rect.Width + spread * 0.5f, rect.Height + spread * 0.85f);
            using (GraphicsPath shadowPath = CreateRoundedRectangle(shadowRect, radius + spread * 0.5f))
            using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(Math.Max(8, 30 - i * 6), 0, 0, 0)))
            {
                graphics.FillPath(shadowBrush, shadowPath);
            }
        }
    }

    private static void DrawCodexFrame(Graphics graphics, int size)
    {
        float width = Math.Max(7.0f, size * 0.018f);
        Color metalDark = Color.FromArgb(255, 80, 86, 100);
        Color metalLight = Color.FromArgb(255, 198, 204, 216);
        Color blueDark = Color.FromArgb(255, 24, 96, 204);
        Color blueLight = Color.FromArgb(255, 50, 140, 255);

        PointF topLeftA = new PointF(size * 0.28f, size * 0.22f);
        PointF topLeftB = new PointF(size * 0.48f, size * 0.22f);
        PointF topRightA = new PointF(size * 0.56f, size * 0.22f);
        PointF topRightB = new PointF(size * 0.78f, size * 0.22f);
        PointF leftDiagA = new PointF(size * 0.20f, size * 0.30f);
        PointF leftDiagB = topLeftA;
        PointF leftVertA = new PointF(size * 0.20f, size * 0.30f);
        PointF leftVertB = new PointF(size * 0.20f, size * 0.68f);
        PointF leftBottomDiagA = leftVertB;
        PointF leftBottomDiagB = new PointF(size * 0.28f, size * 0.76f);
        PointF bottomLeftA = new PointF(size * 0.28f, size * 0.76f);
        PointF bottomLeftB = new PointF(size * 0.46f, size * 0.76f);

        PointF rightDiagA = topRightB;
        PointF rightDiagB = new PointF(size * 0.86f, size * 0.30f);
        PointF rightVertTopA = new PointF(size * 0.86f, size * 0.30f);
        PointF rightVertTopB = new PointF(size * 0.86f, size * 0.48f);
        PointF rightVertBottomA = new PointF(size * 0.86f, size * 0.56f);
        PointF rightVertBottomB = new PointF(size * 0.86f, size * 0.68f);
        PointF rightBottomDiagA = rightVertBottomB;
        PointF rightBottomDiagB = new PointF(size * 0.76f, size * 0.76f);
        PointF bottomRightA = new PointF(size * 0.54f, size * 0.76f);
        PointF bottomRightB = new PointF(size * 0.76f, size * 0.76f);

        DrawBeveledSegment(graphics, leftDiagA, leftDiagB, width, metalDark, metalLight);
        DrawBeveledSegment(graphics, topLeftA, topLeftB, width, metalDark, metalLight);
        DrawBeveledSegment(graphics, leftVertA, leftVertB, width, metalDark, metalLight);
        DrawBeveledSegment(graphics, leftBottomDiagA, leftBottomDiagB, width, metalDark, metalLight);
        DrawBeveledSegment(graphics, bottomLeftA, bottomLeftB, width, metalDark, metalLight);

        DrawBeveledSegment(graphics, topRightA, topRightB, width, blueDark, blueLight);
        DrawBeveledSegment(graphics, rightDiagA, rightDiagB, width, metalDark, metalLight);
        DrawBeveledSegment(graphics, rightVertTopA, rightVertTopB, width, blueDark, blueLight);
        DrawBeveledSegment(graphics, rightVertBottomA, rightVertBottomB, width, blueDark, blueLight);
        DrawBeveledSegment(graphics, rightBottomDiagA, rightBottomDiagB, width, blueDark, blueLight);
        DrawBeveledSegment(graphics, bottomRightA, bottomRightB, width, blueDark, blueLight);
    }

    private static void DrawBeveledSegment(Graphics graphics, PointF start, PointF end, float width, Color baseColor, Color highlightColor)
    {
        float shadowOffset = Math.Max(1.4f, width * 0.36f);
        using (Pen shadowPen = CreateRoundPen(Color.FromArgb(64, 0, 0, 0), width + shadowOffset))
        using (Pen basePen = CreateRoundPen(baseColor, width))
        using (Pen highlightPen = CreateRoundPen(highlightColor, Math.Max(1.6f, width * 0.48f)))
        {
            graphics.DrawLine(shadowPen, start.X + shadowOffset, start.Y + shadowOffset, end.X + shadowOffset, end.Y + shadowOffset);
            graphics.DrawLine(basePen, start, end);
            graphics.DrawLine(highlightPen, start, end);
        }
    }

    private static void DrawTechCurve(Graphics graphics, PointF start, PointF end, PointF control1, PointF control2, float width)
    {
        float shadowOffset = Math.Max(1.4f, width * 0.32f);
        using (Pen shadowPen = CreateRoundPen(Color.FromArgb(70, 0, 0, 0), width + shadowOffset))
        using (Pen basePen = CreateRoundPen(Color.FromArgb(255, 19, 101, 218), width))
        using (Pen highlightPen = CreateRoundPen(Color.FromArgb(255, 62, 150, 255), Math.Max(1.6f, width * 0.54f)))
        {
            graphics.DrawBezier(
                shadowPen,
                start.X + shadowOffset, start.Y + shadowOffset,
                control1.X + shadowOffset, control1.Y + shadowOffset,
                control2.X + shadowOffset, control2.Y + shadowOffset,
                end.X + shadowOffset, end.Y + shadowOffset);

            graphics.DrawBezier(basePen, start, control1, control2, end);
            graphics.DrawBezier(highlightPen, start, control1, control2, end);
        }
    }

    private static void DrawRingNode(Graphics graphics, PointF center, float radius, bool isHub)
    {
        float shadowOffset = Math.Max(1.0f, radius * 0.22f);
        using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(74, 0, 0, 0)))
        {
            graphics.FillEllipse(shadowBrush, center.X - radius + shadowOffset, center.Y - radius + shadowOffset, radius * 2f, radius * 2f);
        }

        float outer = radius;
        float inner = radius * (isHub ? 0.46f : 0.43f);
        using (SolidBrush baseBrush = new SolidBrush(Color.FromArgb(255, 19, 101, 218)))
        using (SolidBrush highlightBrush = new SolidBrush(Color.FromArgb(255, 50, 146, 255)))
        using (SolidBrush centerBrush = new SolidBrush(Color.FromArgb(255, 18, 22, 28)))
        using (Pen edgePen = new Pen(Color.FromArgb(110, 224, 232, 242), Math.Max(1.0f, radius * 0.18f)))
        {
            graphics.FillEllipse(baseBrush, center.X - outer, center.Y - outer, outer * 2f, outer * 2f);
            graphics.FillEllipse(highlightBrush, center.X - outer * 0.84f, center.Y - outer * 0.84f, outer * 1.68f, outer * 1.68f);
            graphics.FillEllipse(centerBrush, center.X - inner, center.Y - inner, inner * 2f, inner * 2f);
            graphics.DrawEllipse(edgePen, center.X - outer, center.Y - outer, outer * 2f, outer * 2f);
        }
    }

    private static void DrawCompactNode(Graphics graphics, PointF center, float radius, bool isHub)
    {
        float inner = radius * (isHub ? 0.42f : 0.48f);
        using (SolidBrush baseBrush = new SolidBrush(Color.FromArgb(255, 47, 140, 255)))
        using (SolidBrush centerBrush = new SolidBrush(Color.FromArgb(255, 20, 24, 30)))
        {
            graphics.FillEllipse(baseBrush, center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
            graphics.FillEllipse(centerBrush, center.X - inner, center.Y - inner, inner * 2f, inner * 2f);
        }
    }

    private static void DrawOrbitRing(Graphics graphics, RectangleF rect, float width, bool compact)
    {
        float shadowOffset = Math.Max(1.0f, width * 0.28f);
        using (Pen shadowPen = CreateRoundPen(Color.FromArgb(compact ? 44 : 58, 0, 0, 0), width + shadowOffset))
        using (Pen basePen = CreateRoundPen(Color.FromArgb(255, 108, 116, 130), width))
        using (Pen highlightPen = CreateRoundPen(Color.FromArgb(255, 206, 214, 226), Math.Max(1.2f, width * 0.42f)))
        using (Pen bluePen = CreateRoundPen(Color.FromArgb(255, 40, 128, 255), width))
        using (Pen blueHighlightPen = CreateRoundPen(Color.FromArgb(255, 79, 156, 255), Math.Max(1.2f, width * 0.42f)))
        {
            graphics.DrawArc(shadowPen, rect.X + shadowOffset, rect.Y + shadowOffset, rect.Width, rect.Height, 0, 360);
            graphics.DrawArc(basePen, rect, 0, 360);
            graphics.DrawArc(highlightPen, rect, 0, 360);
            graphics.DrawArc(bluePen, rect, 288, 124);
            graphics.DrawArc(blueHighlightPen, rect, 288, 124);
        }
    }

    private static void DrawOrbitNode(Graphics graphics, PointF center, float radius, bool isBlue, bool compact)
    {
        float shadowOffset = Math.Max(0.8f, radius * 0.18f);
        using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(compact ? 42 : 60, 0, 0, 0)))
        {
            graphics.FillEllipse(shadowBrush, center.X - radius + shadowOffset, center.Y - radius + shadowOffset, radius * 2f, radius * 2f);
        }

        Color baseColor = isBlue ? Color.FromArgb(255, 40, 128, 255) : Color.FromArgb(255, 118, 126, 142);
        Color highlightColor = isBlue ? Color.FromArgb(255, 82, 158, 255) : Color.FromArgb(255, 220, 226, 235);

        using (SolidBrush baseBrush = new SolidBrush(baseColor))
        using (SolidBrush highlightBrush = new SolidBrush(highlightColor))
        using (Pen edgePen = new Pen(Color.FromArgb(isBlue ? 96 : 110, 236, 240, 247), Math.Max(1.0f, radius * 0.16f)))
        {
            graphics.FillEllipse(baseBrush, center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
            graphics.FillEllipse(highlightBrush, center.X - radius * 0.78f, center.Y - radius * 0.78f, radius * 1.56f, radius * 1.56f);
            graphics.DrawEllipse(edgePen, center.X - radius, center.Y - radius, radius * 2f, radius * 2f);
        }
    }

    private static Pen CreateRoundPen(Color color, float width)
    {
        Pen pen = new Pen(color, width);
        pen.StartCap = LineCap.Round;
        pen.EndCap = LineCap.Round;
        pen.LineJoin = LineJoin.Round;
        return pen;
    }

    private static void FillCircle(Graphics graphics, Color color, float centerX, float centerY, float radius)
    {
        using (SolidBrush brush = new SolidBrush(color))
        {
            graphics.FillEllipse(brush, centerX - radius, centerY - radius, radius * 2f, radius * 2f);
        }
    }

    private static GraphicsPath CreateRoundedRectangle(RectangleF rect, float radius)
    {
        GraphicsPath path = new GraphicsPath();
        path.AddRoundedRectangle(rect, radius);
        return path;
    }

    private static void AddRoundedRectangle(this GraphicsPath path, RectangleF rect, float radius)
    {
        float safeRadius = Math.Max(1f, Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2f));
        float diameter = safeRadius * 2f;
        RectangleF arc = new RectangleF(rect.Location, new SizeF(diameter, diameter));

        path.AddArc(arc, 180, 90);
        arc.X = rect.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = rect.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = rect.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
    }
}
