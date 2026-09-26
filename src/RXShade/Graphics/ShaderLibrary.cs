using System.IO;
using System.Reflection;
using System.Text;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace RXShade.Graphics;

/// <summary>
/// Compiles the embedded HLSL at startup with d3dcompiler_47.dll (inbox on
/// Windows 10/11).
///
/// Runtime compilation rather than build-time fxc, deliberately: it keeps the
/// single-file publish free of loose .cso files, removes the Windows SDK from
/// the build requirements, and lets the shaders be tweaked without a full
/// rebuild. The whole set compiles in well under a second and happens once.
/// </summary>
public sealed class ShaderLibrary : IDisposable
{
    private const string ResourcePrefix = "RXShade.Shaders.";

    private readonly Dictionary<string, ID3D11PixelShader> _pixelShaders = new(StringComparer.Ordinal);
    private readonly string _common;
    private readonly ID3D11Device _device;
    private readonly string _psProfile;

    public ID3D11VertexShader FullscreenVertexShader { get; }

    public ShaderLibrary(GraphicsDevice gfx)
    {
        _device = gfx.Device;

        // Feature level 11 hardware is a given for anything that runs Roblox,
        // but 10.x parts still exist and shader model 4 covers every pass here.
        bool sm5 = gfx.FeatureLevel >= FeatureLevel.Level_11_0;
        _psProfile = sm5 ? "ps_5_0" : "ps_4_0";
        string vsProfile = sm5 ? "vs_5_0" : "vs_4_0";

        _common = LoadSource("Common.hlsli");

        byte[] vsBytecode = CompileRaw(LoadSource("Fullscreen.hlsl"), "VSMain", "Fullscreen.hlsl", vsProfile);
        FullscreenVertexShader = _device.CreateVertexShader(vsBytecode);
    }

    /// <summary>Gets (compiling on first use) the pixel shader for a given .hlsl file.</summary>
    public ID3D11PixelShader GetPixelShader(string fileName)
    {
        if (_pixelShaders.TryGetValue(fileName, out var cached))
            return cached;

        // Common.hlsli is textually prepended instead of #include'd so the
        // compiler never needs an ID3DInclude callback.
        var source = new StringBuilder(_common.Length + 4096);
        source.AppendLine(_common);
        source.AppendLine(LoadSource(fileName));

        byte[] bytecode = CompileRaw(source.ToString(), "PSMain", fileName, _psProfile);
        var shader = _device.CreatePixelShader(bytecode);
        _pixelShaders[fileName] = shader;
        return shader;
    }

    /// <summary>Forces every shader to compile now, so the first captured frame does not stall.</summary>
    public void PrecompileAll()
    {
        foreach (var name in ShaderNames.All)
            GetPixelShader(name);
    }

    private static byte[] CompileRaw(string source, string entryPoint, string sourceName, string profile)
    {
        var result = Compiler.Compile(
            source,
            entryPoint,
            sourceName,
            profile,
            out Blob? bytecode,
            out Blob? errors);

        try
        {
            if (result.Failure || bytecode is null)
            {
                string message = errors?.AsString() ?? result.Description;
                throw new InvalidOperationException(
                    $"Failed to compile shader '{sourceName}' ({profile}):{Environment.NewLine}{message}");
            }

            return bytecode.AsBytes();
        }
        finally
        {
            bytecode?.Dispose();
            errors?.Dispose();
        }
    }

    private static string LoadSource(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        string resourceName = ResourcePrefix + fileName;

        using Stream? stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded shader '{resourceName}' is missing from the assembly.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        foreach (var shader in _pixelShaders.Values)
            shader.Dispose();
        _pixelShaders.Clear();
        FullscreenVertexShader.Dispose();
    }
}

/// <summary>Filenames of the embedded pixel shaders, one per pass.</summary>
public static class ShaderNames
{
    public const string Blit = "Blit.hlsl";
    public const string Sharpen = "Sharpen.hlsl";
    public const string Fxaa = "Fxaa.hlsl";
    public const string BloomPrefilter = "BloomPrefilter.hlsl";
    public const string BloomBlur = "BloomBlur.hlsl";
    public const string BloomComposite = "BloomComposite.hlsl";
    public const string ColorGrade = "ColorGrade.hlsl";
    public const string ChromaticAberration = "ChromaticAberration.hlsl";
    public const string FakeReflection = "FakeReflection.hlsl";
    public const string Focus = "Focus.hlsl";
    public const string FilmGrain = "FilmGrain.hlsl";
    public const string Hdr = "Hdr.hlsl";

    public static readonly string[] All =
    [
        Blit, Sharpen, Fxaa, BloomPrefilter, BloomBlur,
        BloomComposite, ColorGrade, ChromaticAberration, FakeReflection,
        Focus, FilmGrain, Hdr
    ];
}
