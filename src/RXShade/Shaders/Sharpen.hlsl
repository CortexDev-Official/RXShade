// Contrast-Adaptive Sharpening (CAS-style).
//
// Params0.x = sharpness 0..1
//
// Why CAS rather than a plain unsharp mask: a fixed sharpen kernel amplifies
// noise and ringing in already-high-contrast areas (Roblox UI text, skyboxes,
// bright edges). CAS derives a per-pixel sharpening weight from the local
// min/max of the 3x3 neighbourhood, so flat regions get sharpened more and
// regions already near clipping get left alone.
//
// Structure follows AMD's published FidelityFX CAS approach:
//   a b c
//   d e f     e = centre
//   g h i
// The cross (b,d,f,h) drives the sharpening kernel; the corners only widen
// the min/max used for the adaptive weight.

float4 PSMain(PSInput input) : SV_Target
{
    float2 uv = input.uv;
    float sharpness = saturate(Params0.x);

    float3 e = SampleSrc(uv);
    if (sharpness <= 0.0001)
        return float4(e, 1.0);

    float2 t = InvSourceSize;

    float3 a = SampleSrc(uv + float2(-t.x, -t.y));
    float3 b = SampleSrc(uv + float2( 0.0, -t.y));
    float3 c = SampleSrc(uv + float2( t.x, -t.y));
    float3 d = SampleSrc(uv + float2(-t.x,  0.0));
    float3 f = SampleSrc(uv + float2( t.x,  0.0));
    float3 g = SampleSrc(uv + float2(-t.x,  t.y));
    float3 h = SampleSrc(uv + float2( 0.0,  t.y));
    float3 i = SampleSrc(uv + float2( t.x,  t.y));

    // Soft min/max over the cross, then widened by the corners. Both end up
    // in a 0..2 range because the corner term is *added*, not blended - this
    // is what the "2.0 - mx" term below expects.
    float3 mn = min(min(min(d, e), min(f, b)), h);
    mn += min(mn, min(min(a, c), min(g, i)));

    float3 mx = max(max(max(d, e), max(f, b)), h);
    mx += max(mx, max(max(a, c), max(g, i)));

    // Headroom on both ends: how far the neighbourhood is from black and from
    // white. Whichever is tighter limits how hard we may sharpen.
    float3 amp = saturate(min(mn, 2.0 - mx) * rcp(max(mx, 1e-5)));
    amp = sqrt(amp);

    // peak maps sharpness 0..1 onto the kernel's negative lobe.
    float peak = -1.0 / lerp(8.0, 5.0, sharpness);
    float3 w = amp * peak;

    float3 result = (b * w + d * w + f * w + h * w + e) * rcp(1.0 + 4.0 * w);
    return float4(saturate(result), 1.0);
}
