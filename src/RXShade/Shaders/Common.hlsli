// ---------------------------------------------------------------------------
// RXShade - shared shader prologue
//
// This file is textually prepended to every pixel shader by ShaderLibrary,
// which avoids needing an ID3DInclude callback at runtime.
//
// All passes are fullscreen triangles: no vertex buffer, no index buffer,
// three vertices generated from SV_VertexID. Every filter is a pure GPU pass;
// nothing in this pipeline ever touches system memory.
// ---------------------------------------------------------------------------

struct PSInput
{
    float4 position : SV_Position;
    float2 uv       : TEXCOORD0;
};

Texture2D SrcTexture : register(t0);   // primary input for the pass
Texture2D AuxTexture : register(t1);   // secondary input (bloom composite)

SamplerState LinearClamp : register(s0);
SamplerState PointClamp  : register(s1);

cbuffer FrameConstants : register(b0)
{
    float2 SourceSize;      // live content size in pixels
    float2 InvSourceSize;   // 1 / SourceSize  (one texel in UV space)

    // Sub-rectangle of the capture texture we actually want. Scale alone is
    // not enough: capturing a WINDOW yields its whole frame including the
    // title bar, and we only want the client area, which starts at an offset.
    float2 UvScale;
    float2 UvOffset;

    float  Time;            // seconds since capture start, wrapped at 1h
    float  Aspect;          // SourceSize.x / SourceSize.y
    float2 _FramePad;
};

cbuffer EffectConstants : register(b1)
{
    float4 Params0;
    float4 Params1;
    float4 Params2;
    float4 Params3;
};

// Rec.709 relative luminance.
float Luma(float3 c)
{
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}

float3 SampleSrc(float2 uv)
{
    return SrcTexture.SampleLevel(LinearClamp, uv, 0).rgb;
}
