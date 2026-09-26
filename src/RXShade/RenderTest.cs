using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RXShade.Graphics;
using RXShade.Interop;
using RXShade.Models;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace RXShade;

/// <summary>
/// Offline pipeline harness: runs the real shader chain over a SYNTHETIC scene
/// and writes PNGs.
///
/// Deliberately synthetic rather than a screen grab - it never touches the
/// user's actual screen, and a known scene (blocks standing on a floor, a
/// bright sun) makes it obvious whether the reflection is mirroring the right
/// geometry and whether later passes still affect the reflected band.
///
/// Run: RXShade.exe --rendertest &lt;output-dir&gt;
/// </summary>
internal static class RenderTest
{
    private const int Width = 960;
    private const int Height = 540;

    public static int Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        using var gfx = GraphicsDevice.Create();
        using var shaders = new ShaderLibrary(gfx);
        shaders.PrecompileAll();
        using var pipeline = new FilterPipeline(gfx, shaders);

        using var output = new RenderTexture(gfx.Device, Width, Height, Format.B8G8R8A8_UNorm);

        // Two scenes on purpose. A dark corridor flatters every reflection
        // setting; a bright UI-like screen is where an over-eager effect blows
        // the whole frame out to white. Both must look right.
        (string Name, Func<GraphicsDevice, ID3D11Texture2D> Build)[] scenes =
        [
            ("corridor", CreateCorridorScene),
            ("bright", CreateBrightScene)
        ];

        foreach (var (sceneName, build) in scenes)
        {
            using var source = build(gfx);
            using var sourceSrv = gfx.Device.CreateShaderResourceView(source);

            foreach (var (name, settings) in BuildCases())
            {
                var result = pipeline.Process(sourceSrv, Width, Height, 0, 0, Width, Height, settings, 1.7f);
                pipeline.BlitTo(output.Rtv, new Viewport(0, 0, Width, Height, 0, 1), result, clear: false);

                string fileName = $"{sceneName}_{name}";
                string path = Path.Combine(outputDirectory, fileName + ".png");
                SavePng(gfx, output.Texture, path);
                Console.WriteLine($"  {fileName,-36} {result.PassCount,2} passes");
            }
        }

        Console.WriteLine($"Wrote images to {outputDirectory}");
        return 0;
    }

    /// <summary>
    /// A bright, low-contrast screen standing in for the Roblox menu: pale
    /// background, white cards, dark text. This is the case that exposed the
    /// reflection blowing the frame out to white, so it stays in the suite.
    /// </summary>
    private static ID3D11Texture2D CreateBrightScene(GraphicsDevice gfx)
    {
        var pixels = new byte[Width * Height * 4];

        void Set(int x, int y, byte r, byte g, byte b)
        {
            if ((uint)x >= Width || (uint)y >= Height) return;
            int i = (y * Width + x) * 4;
            pixels[i + 0] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 255;
        }

        void FillRect(int x0, int y0, int w, int h, byte r, byte g, byte b)
        {
            for (int y = y0; y < y0 + h; y++)
                for (int x = x0; x < x0 + w; x++)
                    Set(x, y, r, g, b);
        }

        // Near-white page background.
        FillRect(0, 0, Width, Height, 242, 243, 246);

        // Header bar.
        FillRect(0, 0, Width, 46, 255, 255, 255);
        FillRect(24, 16, 120, 14, 60, 64, 74);

        // Grid of game cards with varied thumbnails.
        var swatches = new (byte R, byte G, byte B)[]
        {
            (226, 196, 74), (120, 140, 190), (96, 168, 132),
            (232, 232, 236), (208, 120, 110), (140, 190, 220)
        };

        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 6; col++)
            {
                int x = 24 + col * 152;
                int y = 78 + row * 150;
                var s = swatches[(row * 6 + col) % swatches.Length];

                FillRect(x, y, 136, 92, s.R, s.G, s.B);        // thumbnail
                FillRect(x, y + 100, 110, 10, 70, 74, 84);      // title line
                FillRect(x, y + 116, 64, 8, 150, 154, 162);     // rating line
            }
        }

        return UploadTexture(gfx, pixels);
    }

    private static IEnumerable<(string Name, FilterSnapshot Settings)> BuildCases()
    {
        var s = new FilterSettings();

        s.ApplyPreset(FilterPreset.Off);
        yield return ("00_source", s.Snapshot());

        // Reflection alone. The synthetic floor starts at 58% down the frame,
        // so the horizon slider is set to match it.
        s.ApplyPreset(FilterPreset.Off);
        s.ReflectionEnabled = true;
        s.ReflectionIntensity = 70;
        s.ReflectionHorizon = 55;
        yield return ("01_reflection_only", s.Snapshot());

        // Colour grading alone, pushed hard so the effect is unmistakable.
        s.ApplyPreset(FilterPreset.Off);
        s.ColorGradeEnabled = true;
        s.Saturation = 0;      // fully desaturated
        s.Contrast = 70;
        s.Vignette = 60;
        yield return ("02_colorgrade_only", s.Snapshot());

        // THE KEY CASE: if later passes still apply, this must be greyscale
        // EVERYWHERE, including inside the reflected band.
        s.ApplyPreset(FilterPreset.Off);
        s.ReflectionEnabled = true;
        s.ReflectionIntensity = 70;
        s.ReflectionHorizon = 55;
        s.ColorGradeEnabled = true;
        s.Saturation = 0;
        s.Contrast = 70;
        s.Vignette = 60;
        yield return ("03_reflection_plus_colorgrade", s.Snapshot());

        s.ApplyPreset(FilterPreset.Water);
        yield return ("04_preset_water", s.Snapshot());

        s.ApplyPreset(FilterPreset.Cinematic);
        yield return ("05_preset_cinematic", s.Snapshot());

        s.ApplyPreset(FilterPreset.HDR);
        yield return ("06_preset_hdr", s.Snapshot());

        // Worst case: reflection cranked to maximum. On the bright scene this
        // must NOT wash out to white.
        s.ApplyPreset(FilterPreset.Off);
        s.ReflectionEnabled = true;
        s.ReflectionSheen = 100;
        s.ReflectionShine = 100;
        s.ReflectionSharpness = 100;
        s.ReflectionIntensity = 100;
        yield return ("07_reflection_maxed", s.Snapshot());
    }

    /// <summary>
    /// A perspective-correct wooden corridor with regularly spaced ceiling
    /// lamps and wall sconces - deliberately modelled on the Roblox "Doors"
    /// hotel interiors this filter is aimed at.
    ///
    /// An indoor corridor is the right test case because it exercises all four
    /// surfaces at once: the reflection has to streak lights down the floor,
    /// along both walls, and across the ceiling. An outdoor scene only ever
    /// tests the floor.
    /// </summary>
    private static ID3D11Texture2D CreateCorridorScene(GraphicsDevice gfx)
    {
        const int Floor = 0, Ceiling = 1, LeftWall = 2, RightWall = 3;

        var pixels = new byte[Width * Height * 4];
        float aspect = (float)Width / Height;

        for (int py = 0; py < Height; py++)
        {
            for (int px = 0; px < Width; px++)
            {
                // Camera ray through this pixel, looking down +Z.
                float u = ((px + 0.5f) / Width * 2f - 1f) * aspect * 0.55f;
                float v = ((py + 0.5f) / Height * 2f - 1f) * 0.55f;

                // Nearest hit among the four corridor planes.
                float best = float.MaxValue;
                int surface = Floor;

                void Test(float t, int which)
                {
                    if (t > 0.001f && t < best) { best = t; surface = which; }
                }

                if (v > 0.001f) Test(0.5f / v, Floor);
                if (v < -0.001f) Test(-0.5f / v, Ceiling);
                if (u < -0.001f) Test(-0.95f / u, LeftWall);
                if (u > 0.001f) Test(0.95f / u, RightWall);

                float z = best;                 // distance along the corridor
                float hitX = best * u;
                float hitY = best * v;

                // Base wood tones, darker on floor and ceiling than the walls.
                byte r, g, b;
                switch (surface)
                {
                    case Floor: r = 116; g = 62; b = 40; break;
                    case Ceiling: r = 96; g = 52; b = 34; break;
                    default: r = 142; g = 84; b = 56; break;
                }

                // Distance falloff so the far end of the corridor goes dark.
                float attenuation = 1f / (1f + z * 0.30f);

                // Ceiling lamps every 3 units, and wall sconces offset between them.
                bool isLamp = false;
                float zPhase = z - MathF.Floor(z / 3f) * 3f;

                if (surface == Ceiling && MathF.Abs(hitX) < 0.20f && zPhase < 0.45f)
                    isLamp = true;
                if ((surface == LeftWall || surface == RightWall) &&
                    hitY > -0.22f && hitY < -0.02f && zPhase > 1.5f && zPhase < 1.95f)
                    isLamp = true;

                if (isLamp)
                {
                    // Emissive: bright enough to drive both bloom and the
                    // reflection's bright-pass, and barely dimmed by distance.
                    float lampFade = 0.55f + 0.45f * attenuation;
                    r = (byte)Math.Min(255, 255 * lampFade);
                    g = (byte)Math.Min(255, 244 * lampFade);
                    b = (byte)Math.Min(255, 214 * lampFade);
                }
                else
                {
                    r = (byte)(r * attenuation);
                    g = (byte)(g * attenuation);
                    b = (byte)(b * attenuation);
                }

                int i = (py * Width + px) * 4;
                pixels[i + 0] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = 255;
            }
        }

        return UploadTexture(gfx, pixels);
    }

    private static ID3D11Texture2D UploadTexture(GraphicsDevice gfx, byte[] pixels)
    {
        var description = new Texture2DDescription
        {
            Width = Width,
            Height = Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        };

        GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var data = new SubresourceData(handle.AddrOfPinnedObject(), (uint)(Width * 4));
            return gfx.Device.CreateTexture2D(description, new[] { data });
        }
        finally
        {
            handle.Free();
        }
    }

    private static void SavePng(GraphicsDevice gfx, ID3D11Texture2D texture, string path)
    {
        var description = texture.Description;
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        description.MiscFlags = ResourceOptionFlags.None;

        using var staging = gfx.Device.CreateTexture2D(description);
        gfx.Context.CopyResource(staging, texture);

        var mapped = gfx.Context.Map(staging, 0, MapMode.Read);
        try
        {
            int width = (int)description.Width;
            int height = (int)description.Height;
            int stride = width * 4;
            var buffer = new byte[stride * height];

            for (int y = 0; y < height; y++)
            {
                IntPtr row = mapped.DataPointer + y * (int)mapped.RowPitch;
                Marshal.Copy(row, buffer, y * stride, stride);
            }

            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, buffer, stride);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        finally
        {
            gfx.Context.Unmap(staging, 0);
        }
    }
}
