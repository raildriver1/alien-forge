namespace AlienForge.Core.Imaging;

/// <summary>
/// Pixel-level comparison of two images, for checking a decoder against a known
/// good reference rather than eyeballing the output.
/// </summary>
public readonly record struct ImageDiff(
    bool SizeMatches,
    int Width,
    int Height,
    int MaxDelta,
    double MeanAbsError,
    double DifferingPixelPercent,
    int MaxDeltaIgnoringAlpha,
    double DifferingPixelPercentIgnoringAlpha)
{
    public bool Identical => SizeMatches && MaxDelta == 0;
    public bool IdenticalIgnoringAlpha => SizeMatches && MaxDeltaIgnoringAlpha == 0;

    public override string ToString()
    {
        if (!SizeMatches)
            return "размеры не совпадают";
        if (Identical)
            return "идентично";
        if (IdenticalIgnoringAlpha)
            return $"идентично по RGB (альфа отличается, макс {MaxDelta})";
        return $"макс дельта {MaxDeltaIgnoringAlpha} (RGB), отличается {DifferingPixelPercentIgnoringAlpha:F3}% пикселей, " +
               $"средняя ошибка {MeanAbsError:F4}";
    }

    public static ImageDiff Compare(RgbaImage a, RgbaImage b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            return new ImageDiff(false, a.Width, a.Height, 255, 255, 100, 255, 100);

        int maxDelta = 0;
        int maxDeltaRgb = 0;
        long sum = 0;
        int differing = 0;
        int differingRgb = 0;
        int pixels = a.Width * a.Height;

        for (int i = 0; i < a.Pixels.Length; i += 4)
        {
            int dr = Math.Abs(a.Pixels[i + 0] - b.Pixels[i + 0]);
            int dg = Math.Abs(a.Pixels[i + 1] - b.Pixels[i + 1]);
            int db = Math.Abs(a.Pixels[i + 2] - b.Pixels[i + 2]);
            int da = Math.Abs(a.Pixels[i + 3] - b.Pixels[i + 3]);

            int rgb = Math.Max(dr, Math.Max(dg, db));
            int all = Math.Max(rgb, da);

            maxDeltaRgb = Math.Max(maxDeltaRgb, rgb);
            maxDelta = Math.Max(maxDelta, all);
            sum += dr + dg + db + da;
            if (all != 0)
                differing++;
            if (rgb != 0)
                differingRgb++;
        }

        return new ImageDiff(
            SizeMatches: true,
            Width: a.Width,
            Height: a.Height,
            MaxDelta: maxDelta,
            MeanAbsError: sum / (double)(pixels * 4),
            DifferingPixelPercent: differing * 100.0 / pixels,
            MaxDeltaIgnoringAlpha: maxDeltaRgb,
            DifferingPixelPercentIgnoringAlpha: differingRgb * 100.0 / pixels);
    }
}
