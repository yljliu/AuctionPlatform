using SkiaSharp;

namespace Auction.Infrastructure.Seed;

/// <summary>What a demo picture shows.</summary>
public enum PictureKind
{
    Watch,
    Painting,
    Print,
    Coin,
    Card,
    Pumpkin,     // Halloween
    Ornament,    // Christmas
}

/// <summary>
/// Draws simple product pictures for the demo catalog with SkiaSharp (no text, so no fonts are needed in the
/// container). The same item key always gives the same picture. JPEG, 1200 x 900.
/// </summary>
public static class DemoPictures
{
    public const int Width = 1200;
    public const int Height = 900;

    /// <param name="variant">0 = front view, 1 = a closer look in other light.</param>
    public static byte[] Draw(PictureKind kind, string key, int variant)
    {
        var random = new Random(StableSeed(key) + variant);
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        var canvas = surface.Canvas;

        var hue = random.Next(0, 360);
        Background(canvas, hue, variant);

        canvas.Save();
        if (variant == 1)
        {
            // Closer look: zoom in a little and turn slightly.
            canvas.Translate(Width / 2f, Height / 2f);
            canvas.Scale(1.25f);
            canvas.RotateDegrees(random.Next(-8, 8));
            canvas.Translate(-Width / 2f, -Height / 2f);
        }

        switch (kind)
        {
            case PictureKind.Watch: Watch(canvas, random, hue); break;
            case PictureKind.Painting: Painting(canvas, random, hue); break;
            case PictureKind.Print: Print(canvas, random, hue); break;
            case PictureKind.Coin: Coin(canvas, random, hue); break;
            case PictureKind.Pumpkin: Pumpkin(canvas, random); break;
            case PictureKind.Ornament: Ornament(canvas, random, hue); break;
            default: Card(canvas, random, hue); break;
        }

        canvas.Restore();

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    private static void Background(SKCanvas canvas, int hue, int variant)
    {
        using var paint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, Height),
                [Hsv(hue, 12, variant == 0 ? 96 : 88), Hsv(hue, 20, variant == 0 ? 78 : 64)], SKShaderTileMode.Clamp),
        };
        canvas.DrawRect(0, 0, Width, Height, paint);

        // A soft shadow under the object.
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 40), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 30) };
        canvas.DrawOval(Width / 2f, Height * 0.84f, 360, 40, shadow);
    }

    private static void Watch(SKCanvas canvas, Random random, int hue)
    {
        var cx = Width / 2f;
        var cy = Height / 2f;
        var strap = Hsv((hue + 30) % 360, 45, random.Next(25, 50));
        using var strapPaint = Fill(strap);
        canvas.DrawRoundRect(cx - 110, 40, 220, Height - 120, 40, 40, strapPaint);

        using var caseMetal = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(new SKPoint(cx - 60, cy - 60), 320,
                [new SKColor(245, 245, 245), new SKColor(150, 150, 155), new SKColor(90, 90, 95)], SKShaderTileMode.Clamp),
        };
        canvas.DrawCircle(cx, cy, 250, caseMetal);
        canvas.DrawRoundRect(cx + 235, cy - 30, 50, 60, 12, 12, caseMetal);   // crown

        var dial = random.Next(3) switch { 0 => new SKColor(20, 22, 30), 1 => new SKColor(240, 236, 225), _ => Hsv(hue, 60, 45) };
        using var dialPaint = Fill(dial);
        canvas.DrawCircle(cx, cy, 205, dialPaint);

        var light = dial.Red + dial.Green + dial.Blue > 380;
        using var marks = Stroke(light ? new SKColor(40, 40, 40) : new SKColor(235, 235, 235), 10);
        for (var i = 0; i < 12; i++)
        {
            var a = i * Math.PI / 6;
            var inner = i % 3 == 0 ? 150 : 170;
            canvas.DrawLine(cx + (float)(Math.Sin(a) * inner), cy - (float)(Math.Cos(a) * inner),
                cx + (float)(Math.Sin(a) * 190), cy - (float)(Math.Cos(a) * 190), marks);
        }

        using var hands = Stroke(light ? new SKColor(25, 25, 25) : new SKColor(250, 250, 250), 14);
        Hand(canvas, cx, cy, random.Next(0, 360), 110, hands);
        Hand(canvas, cx, cy, random.Next(0, 360), 165, hands);
        using var seconds = Stroke(new SKColor(210, 40, 40), 5);
        Hand(canvas, cx, cy, random.Next(0, 360), 180, seconds);
        using var pin = Fill(new SKColor(200, 200, 200));
        canvas.DrawCircle(cx, cy, 14, pin);
    }

    private static void Hand(SKCanvas canvas, float cx, float cy, double degrees, float length, SKPaint paint)
    {
        var a = degrees * Math.PI / 180;
        canvas.DrawLine(cx, cy, cx + (float)(Math.Sin(a) * length), cy - (float)(Math.Cos(a) * length), paint);
    }

    private static void Frame(SKCanvas canvas, SKRect inner, Random random)
    {
        var wood = random.Next(2) == 0 ? new SKColor(120, 80, 40) : new SKColor(195, 160, 75);
        using var framePaint = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(inner.Left, inner.Top), new SKPoint(inner.Right, inner.Bottom),
                [wood, Darker(wood, 0.6f), wood], SKShaderTileMode.Clamp),
        };
        var outer = SKRect.Inflate(inner, 45, 45);
        canvas.DrawRect(outer, framePaint);
    }

    private static void Painting(SKCanvas canvas, Random random, int hue)
    {
        var art = new SKRect(270, 160, 930, 680);
        Frame(canvas, art, random);
        canvas.Save();
        canvas.ClipRect(art);

        using var sky = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, art.Top), new SKPoint(0, art.Bottom),
                [Hsv((hue + 190) % 360, 45, 90), Hsv((hue + 30) % 360, 50, 95)], SKShaderTileMode.Clamp),
        };
        canvas.DrawRect(art, sky);

        using var sun = Fill(Hsv(40, 70, 100));
        canvas.DrawCircle(art.Left + random.Next(120, 540), art.Top + random.Next(80, 180), random.Next(35, 60), sun);

        for (var layer = 0; layer < 3; layer++)
        {
            using var hill = Fill(Hsv((hue + 90 + layer * 15) % 360, 45 + layer * 10, 70 - layer * 18));
            using var path = new SKPath();
            var baseY = art.Top + 300 + layer * 70;
            path.MoveTo(art.Left, art.Bottom);
            for (var x = 0; x <= 660; x += 60)
            {
                path.LineTo(art.Left + x, baseY - random.Next(0, 90));
            }

            path.LineTo(art.Right, art.Bottom);
            path.Close();
            canvas.DrawPath(path, hill);
        }

        canvas.Restore();
    }

    private static void Print(SKCanvas canvas, Random random, int hue)
    {
        var art = new SKRect(300, 140, 900, 700);
        Frame(canvas, art, random);
        using var paper = Fill(new SKColor(244, 240, 230));
        canvas.DrawRect(art, paper);

        var picture = SKRect.Inflate(art, -50, -50);
        using var sky = Fill(Hsv((hue + 200) % 360, 35, 85));
        canvas.DrawRect(picture, sky);

        // City skyline.
        var x = picture.Left;
        while (x < picture.Right)
        {
            var w = random.Next(30, 70);
            var h = random.Next(80, 330);
            using var building = Fill(Hsv((hue + 220) % 360, 25, random.Next(20, 45)));
            canvas.DrawRect(x, picture.Bottom - h, Math.Min(w, picture.Right - x), h, building);
            using var window = Fill(new SKColor(250, 220, 120, 200));
            for (var wy = picture.Bottom - h + 15; wy < picture.Bottom - 15; wy += 28)
            {
                if (random.Next(3) == 0)
                {
                    canvas.DrawRect(x + 8, wy, 8, 12, window);
                }
            }

            x += w + 4;
        }

        using var signature = Stroke(new SKColor(60, 60, 60), 3);
        canvas.DrawLine(picture.Right - 140, art.Bottom - 25, picture.Right - 20, art.Bottom - 32, signature);
    }

    private static void Coin(SKCanvas canvas, Random random, int hue)
    {
        var cx = Width / 2f;
        var cy = Height / 2f;
        var metal = random.Next(3) switch
        {
            0 => (Light: new SKColor(250, 225, 150), Dark: new SKColor(170, 120, 40)),    // gold
            1 => (Light: new SKColor(235, 235, 240), Dark: new SKColor(130, 130, 140)),   // silver
            _ => (Light: new SKColor(230, 160, 110), Dark: new SKColor(140, 70, 35)),     // copper
        };
        using var face = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(new SKPoint(cx - 80, cy - 90), 380, [metal.Light, metal.Dark], SKShaderTileMode.Clamp),
        };
        canvas.DrawCircle(cx, cy, 300, face);

        using var rim = Stroke(Darker(metal.Dark, 0.8f), 18);
        canvas.DrawCircle(cx, cy, 285, rim);
        using var beads = Fill(Darker(metal.Dark, 0.7f));
        for (var i = 0; i < 60; i++)
        {
            var a = i * Math.PI / 30;
            canvas.DrawCircle(cx + (float)(Math.Sin(a) * 255), cy - (float)(Math.Cos(a) * 255), 5, beads);
        }

        // A raised profile: an oval head and shoulders.
        using var relief = new SKPaint { IsAntialias = true, Color = Darker(metal.Light, 0.85f) };
        canvas.DrawOval(cx + 10, cy - 40, 95, 120, relief);
        canvas.DrawRoundRect(cx - 120, cy + 60, 260, 110, 50, 50, relief);
        using var edge = Stroke(Darker(metal.Dark, 0.9f), 4);
        canvas.DrawOval(cx + 10, cy - 40, 95, 120, edge);
        _ = hue;
        _ = random;
    }

    private static void Card(SKCanvas canvas, Random random, int hue)
    {
        var card = new SKRect(390, 90, 810, 680);
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 70), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 18) };
        canvas.DrawRoundRect(SKRect.Inflate(card, 10, 10), 30, 30, shadow);

        // A clear graded case around the card.
        using var slab = Fill(new SKColor(235, 240, 245, 230));
        canvas.DrawRoundRect(SKRect.Inflate(card, 30, 70), 24, 24, slab);
        using var label = Fill(Hsv((hue + 180) % 360, 55, 70));
        canvas.DrawRoundRect(card.Left - 20, card.Top - 60, card.Width + 40, 45, 10, 10, label);

        using var border = Fill(Hsv(hue, 65, 75));
        canvas.DrawRoundRect(card, 18, 18, border);
        var photo = new SKRect(card.Left + 30, card.Top + 30, card.Right - 30, card.Bottom - 130);
        using var field = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, photo.Top), new SKPoint(0, photo.Bottom),
                [Hsv((hue + 200) % 360, 40, 90), Hsv(110, 50, 60)], SKShaderTileMode.Clamp),
        };
        canvas.DrawRect(photo, field);

        // A player shape.
        using var player = Fill(Hsv((hue + 20) % 360, 70, random.Next(35, 60)));
        canvas.DrawCircle(photo.MidX, photo.Top + 110, 45, player);
        canvas.DrawRoundRect(photo.MidX - 70, photo.Top + 165, 140, 200, 40, 40, player);
        using var nameBar = Fill(new SKColor(250, 250, 250));
        canvas.DrawRoundRect(card.Left + 40, card.Bottom - 105, card.Width - 80, 70, 12, 12, nameBar);
    }

    /// <summary>A carved pumpkin: orange ribs, a green stem, a lit face.</summary>
    private static void Pumpkin(SKCanvas canvas, Random random)
    {
        var cx = Width / 2f;
        var cy = Height / 2f + 40;
        var orange = Hsv(random.Next(22, 34), 85, random.Next(85, 98));
        using var body = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(new SKPoint(cx - 80, cy - 90), 420, [orange, Darker(orange, 0.6f)], SKShaderTileMode.Clamp),
        };

        // Five overlapping ribs make the round, ridged shape.
        foreach (var (dx, w) in new[] { (-210f, 190f), (210f, 190f), (-110f, 220f), (110f, 220f), (0f, 240f) })
        {
            canvas.DrawOval(cx + dx, cy, w, 280, body);
        }

        using var stem = Fill(Hsv(100, 55, random.Next(30, 45)));
        canvas.DrawRoundRect(cx - 28, cy - 360, 56, 110, 18, 18, stem);

        // The face glows, as if a candle is inside.
        using var glow = new SKPaint { Color = Hsv(48, 80, 100), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Solid, 6) };
        using var eye = new SKPath();
        foreach (var ex in new[] { cx - 120, cx + 120 })
        {
            eye.MoveTo(ex, cy - 110);
            eye.LineTo(ex - 55, cy - 20);
            eye.LineTo(ex + 55, cy - 20);
            eye.Close();
        }

        canvas.DrawPath(eye, glow);
        using var mouth = new SKPath();
        mouth.MoveTo(cx - 190, cy + 60);
        for (var i = 0; i <= 6; i++)
        {
            mouth.LineTo(cx - 190 + (i * 380 / 6f), cy + (i % 2 == 0 ? 60 : 110));
        }

        mouth.LineTo(cx + 150, cy + 170);
        mouth.LineTo(cx - 150, cy + 170);
        mouth.Close();
        canvas.DrawPath(mouth, glow);
    }

    /// <summary>A shiny Christmas ball with a cap and a stripe.</summary>
    private static void Ornament(SKCanvas canvas, Random random, int hue)
    {
        var cx = Width / 2f;
        var cy = Height / 2f + 40;
        var color = random.Next(3) switch { 0 => Hsv(0, 80, 75), 1 => Hsv(140, 70, 55), _ => Hsv(hue, 70, 70) };

        using var thread = Stroke(new SKColor(200, 170, 60), 6);
        canvas.DrawLine(cx, 0, cx, cy - 300, thread);
        using var cap = Fill(new SKColor(205, 175, 70));
        canvas.DrawRoundRect(cx - 50, cy - 320, 100, 60, 10, 10, cap);

        using var ball = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(new SKPoint(cx - 90, cy - 100), 380, [new SKColor(255, 255, 255), color, Darker(color, 0.5f)],
                [0f, 0.25f, 1f], SKShaderTileMode.Clamp),
        };
        canvas.DrawCircle(cx, cy, 270, ball);

        using var stripe = Stroke(new SKColor(240, 215, 120), 26);
        canvas.Save();
        using var clip = new SKPath();
        clip.AddCircle(cx, cy, 270);
        canvas.ClipPath(clip, antialias: true);
        canvas.DrawLine(cx - 300, cy + 40, cx + 300, cy - 40, stripe);
        canvas.Restore();
    }

    private static SKPaint Fill(SKColor color) => new() { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };

    private static SKPaint Stroke(SKColor color, float width) =>
        new() { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width, StrokeCap = SKStrokeCap.Round };

    private static SKColor Hsv(int h, int s, int v) => SKColor.FromHsv(h, s, v);

    private static SKColor Darker(SKColor c, float f) => new((byte)(c.Red * f), (byte)(c.Green * f), (byte)(c.Blue * f), c.Alpha);

    private static int StableSeed(string key)
    {
        var hash = 17;
        foreach (var ch in key)
        {
            hash = unchecked((hash * 31) + ch);
        }

        return hash & 0x7FFFFFFF;
    }
}
