// Bloom stage 2 - separable gaussian blur.
//
// Params0.xy = step vector in UV space (already texel- and radius-scaled).
//              The pipeline runs this twice: once horizontal, once vertical.
//
// A 9-tap gaussian collapsed into 5 texture fetches. The trick is that a
// bilinear sample placed *between* two texels returns their weighted average
// for free, so one fetch at offset 1.3846 covers taps 1 and 2, and one at
// 3.2308 covers taps 3 and 4. Separable + linear-tap means a 9x9 blur costs
// 10 fetches instead of 81.

static const float kOffset[3] = { 0.0, 1.3846153846, 3.2307692308 };
static const float kWeight[3] = { 0.2270270270, 0.3162162162, 0.0702702703 };

float4 PSMain(PSInput input) : SV_Target
{
    float2 uv = input.uv;
    float2 step = Params0.xy;

    float3 result = SampleSrc(uv) * kWeight[0];

    [unroll]
    for (int i = 1; i < 3; i++)
    {
        float2 o = step * kOffset[i];
        result += SampleSrc(uv + o) * kWeight[i];
        result += SampleSrc(uv - o) * kWeight[i];
    }

    return float4(result, 1.0);
}
