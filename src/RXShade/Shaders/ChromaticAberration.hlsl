// Chromatic aberration - transverse (lateral) only.
//
// Params0.x = amount 0..1
//
// Real lens CA scales with distance from the optical axis, so the offset is
// weighted by radius. The frame centre stays perfectly sharp and the effect
// only shows up towards the corners, which is what stops it reading as "the
// image is broken" at low intensities.
//
// Kept deliberately subtle: the maximum offset is ~1.2% of frame width even at
// 100%, because this effect competes directly with legibility of Roblox UI.

float4 PSMain(PSInput input) : SV_Target
{
    float2 uv = input.uv;
    float amount = saturate(Params0.x);

    if (amount <= 0.0001)
        return float4(SampleSrc(uv), 1.0);

    float2 dir = uv - 0.5;
    float radius = length(dir);

    // radius^2 falloff concentrates the split in the outer third of the frame.
    float2 offset = dir * radius * amount * 0.012;

    float r = SrcTexture.SampleLevel(LinearClamp, uv + offset, 0).r;
    float g = SrcTexture.SampleLevel(LinearClamp, uv,          0).g;
    float b = SrcTexture.SampleLevel(LinearClamp, uv - offset, 0).b;

    return float4(r, g, b, 1.0);
}
