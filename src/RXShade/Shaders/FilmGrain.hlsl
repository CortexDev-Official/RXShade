// Animated film grain.
//
// Params0 = (intensity, coarseness, 0, 0)
//
// Two details make grain read as film rather than as "video noise":
//
//  1. It is quantised into square cells sized in PIXELS, not UV. Without that,
//     the grain would get finer as resolution rises and would look completely
//     different at 1080p and 1440p.
//
//  2. It is weighted by luminance. Real film has almost no visible grain in
//     blown-out highlights or crushed blacks - it peaks in the mid-tones.
//     Uniform noise instantly looks synthetic, especially over a bright sky.

float Hash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float4 PSMain(PSInput input) : SV_Target
{
    float2 uv = input.uv;
    float3 c = SampleSrc(uv);

    float intensity = saturate(Params0.x);
    if (intensity <= 0.0001)
        return float4(c, 1.0);

    // Cell size 1..3.5 px. Larger cells read as faster/older film stock.
    float cell = lerp(1.0, 3.5, saturate(Params0.y));
    float2 grainCoord = floor(uv * SourceSize / cell);

    // A small per-frame seed rather than adding Time straight onto the
    // coordinates: Time reaches 3600 and would destroy frac() precision.
    float seed = frac(Time * 37.4159) * 128.0;
    float noise = Hash21(grainCoord + seed) - 0.5;

    float luma = Luma(max(c, 0.0));
    float midtoneWeight = 1.0 - abs(saturate(luma) * 2.0 - 1.0);

    c += noise * intensity * 0.22 * midtoneWeight;
    return float4(saturate(c), 1.0);
}
