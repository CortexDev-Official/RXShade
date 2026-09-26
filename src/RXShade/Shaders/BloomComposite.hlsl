// Bloom stage 3 - additive blend back onto the full-resolution frame.
//
// SrcTexture = original frame (full res)
// AuxTexture = blurred bright-pass (quarter res, upsampled by the sampler)
//
// Params0.x = intensity
//
// NOTE ON "REFLECTIONS": boosting bright highlights like this is the closest a
// capture-only tool can honestly get to a specular/reflection look. It is a
// glow around already-bright pixels - it carries no scene information. See
// FakeReflection.hlsl for the full explanation of why real SSR is impossible
// from outside the engine.

float4 PSMain(PSInput input) : SV_Target
{
    float2 uv = input.uv;

    float3 base  = SrcTexture.SampleLevel(LinearClamp, uv, 0).rgb;
    float3 bloom = AuxTexture.SampleLevel(LinearClamp, uv, 0).rgb;

    return float4(base + bloom * Params0.x, 1.0);
}
