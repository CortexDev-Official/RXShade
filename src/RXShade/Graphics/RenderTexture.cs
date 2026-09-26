using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RXShade.Graphics;

/// <summary>A render target + matching shader resource view, used for ping-pong passes.</summary>
public sealed class RenderTexture : IDisposable
{
    public ID3D11Texture2D Texture { get; }
    public ID3D11RenderTargetView Rtv { get; }
    public ID3D11ShaderResourceView Srv { get; }
    public int Width { get; }
    public int Height { get; }
    public Format Format { get; }

    public RenderTexture(ID3D11Device device, int width, int height, Format format)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Format = format;

        Texture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        });

        Rtv = device.CreateRenderTargetView(Texture);
        Srv = device.CreateShaderResourceView(Texture);
    }

    public void Dispose()
    {
        Srv.Dispose();
        Rtv.Dispose();
        Texture.Dispose();
    }
}
