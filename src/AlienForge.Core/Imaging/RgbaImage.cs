namespace AlienForge.Core.Imaging;

/// <summary>
/// Straight 8-bit RGBA pixel buffer, top-down, no padding.
/// </summary>
public sealed class RgbaImage
{
    public RgbaImage(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Размеры должны быть положительными.");
        Width = width;
        Height = height;
        Pixels = new byte[checked(width * height * 4)];
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public int Stride => Width * 4;

    public void Set(int x, int y, byte r, byte g, byte b, byte a)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            return;
        int i = (y * Width + x) * 4;
        Pixels[i + 0] = r;
        Pixels[i + 1] = g;
        Pixels[i + 2] = b;
        Pixels[i + 3] = a;
    }

    /// <summary>
    /// Rebuilds the blue channel of a two-channel tangent-space normal map.
    /// </summary>
    /// <remarks>
    /// DXN stores only X and Y, because a unit normal's Z is implied:
    /// z = sqrt(1 - x^2 - y^2). Without this step the map reads as a flat
    /// red/green image and lighting comes out wrong rather than merely dark.
    /// </remarks>
    public void ReconstructNormalZ()
    {
        for (int i = 0; i < Pixels.Length; i += 4)
        {
            float x = Pixels[i + 0] / 127.5f - 1.0f;
            float y = Pixels[i + 1] / 127.5f - 1.0f;
            float zz = 1.0f - x * x - y * y;
            float z = zz > 0.0f ? MathF.Sqrt(zz) : 0.0f;
            Pixels[i + 2] = (byte)Math.Clamp((int)MathF.Round((z + 1.0f) * 127.5f), 0, 255);
            Pixels[i + 3] = 255;
        }
    }

    /// <summary>Forces alpha to opaque. Used for maps where alpha carries no coverage.</summary>
    public void MakeOpaque()
    {
        for (int i = 3; i < Pixels.Length; i += 4)
            Pixels[i] = 255;
    }

    public bool HasMeaningfulAlpha()
    {
        for (int i = 3; i < Pixels.Length; i += 4)
            if (Pixels[i] != 255)
                return true;
        return false;
    }
}
