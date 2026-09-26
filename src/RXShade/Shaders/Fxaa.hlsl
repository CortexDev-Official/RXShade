// FXAA - Fast Approximate Anti-Aliasing.
//
// Params0.x = blend strength 0..1
//
// Post-process AA is the only kind available to a capture-only tool: we have
// colour and nothing else. No depth, no motion vectors, no MSAA samples. FXAA
// finds edges from luminance alone, works out which way the edge runs, walks
// along it to find its ends, and shifts the sample point across the edge by an
// amount proportional to where this pixel sits along it.
//
// Structure follows Timothy Lottes' FXAA 3.11 quality path.

#define FXAA_EDGE_THRESHOLD     0.125
#define FXAA_EDGE_THRESHOLD_MIN 0.0312
#define FXAA_SEARCH_STEPS       12
#define FXAA_SUBPIX_QUALITY     0.75

// Step multipliers: fine near the origin, coarse further out, so long edges
// terminate in a bounded number of taps.
static const float FXAA_STEP[FXAA_SEARCH_STEPS] =
{
    1.0, 1.0, 1.0, 1.0, 1.0, 1.5, 2.0, 2.0, 2.0, 4.0, 8.0, 8.0
};

// sqrt() approximates a perceptual response cheaply; FXAA only needs a
// monotonic luma, not a colorimetric one.
float FxaaLuma(float3 c)
{
    return sqrt(Luma(c));
}

float4 PSMain(PSInput input) : SV_Target
{
    float2 uv = input.uv;
    float3 colorCenter = SampleSrc(uv);

    float strength = saturate(Params0.x);
    if (strength <= 0.0001)
        return float4(colorCenter, 1.0);

    float2 t = InvSourceSize;

    float lumaC = FxaaLuma(colorCenter);
    float lumaD = FxaaLuma(SampleSrc(uv + float2( 0.0,  t.y)));
    float lumaU = FxaaLuma(SampleSrc(uv + float2( 0.0, -t.y)));
    float lumaL = FxaaLuma(SampleSrc(uv + float2(-t.x,  0.0)));
    float lumaR = FxaaLuma(SampleSrc(uv + float2( t.x,  0.0)));

    float lumaMin = min(lumaC, min(min(lumaD, lumaU), min(lumaL, lumaR)));
    float lumaMax = max(lumaC, max(max(lumaD, lumaU), max(lumaL, lumaR)));
    float lumaRange = lumaMax - lumaMin;

    // Flat area, or so dark that any edge here is invisible: leave it alone.
    // This early-out is what keeps FXAA cheap on a full 1080p frame.
    if (lumaRange < max(FXAA_EDGE_THRESHOLD_MIN, lumaMax * FXAA_EDGE_THRESHOLD))
        return float4(colorCenter, 1.0);

    float lumaDL = FxaaLuma(SampleSrc(uv + float2(-t.x,  t.y)));
    float lumaUR = FxaaLuma(SampleSrc(uv + float2( t.x, -t.y)));
    float lumaUL = FxaaLuma(SampleSrc(uv + float2(-t.x, -t.y)));
    float lumaDR = FxaaLuma(SampleSrc(uv + float2( t.x,  t.y)));

    float lumaDU = lumaD + lumaU;
    float lumaLR = lumaL + lumaR;
    float lumaLCorners = lumaDL + lumaUL;
    float lumaDCorners = lumaDL + lumaDR;
    float lumaRCorners = lumaDR + lumaUR;
    float lumaUCorners = lumaUR + lumaUL;

    // Second-derivative estimates along each axis; the larger one is the axis
    // the edge crosses, so the edge itself runs along the other one.
    float edgeH = abs(-2.0 * lumaL + lumaLCorners)
                + abs(-2.0 * lumaC + lumaDU) * 2.0
                + abs(-2.0 * lumaR + lumaRCorners);
    float edgeV = abs(-2.0 * lumaU + lumaUCorners)
                + abs(-2.0 * lumaC + lumaLR) * 2.0
                + abs(-2.0 * lumaD + lumaDCorners);

    bool isHorizontal = edgeH >= edgeV;

    float luma1 = isHorizontal ? lumaD : lumaL;
    float luma2 = isHorizontal ? lumaU : lumaR;
    float gradient1 = luma1 - lumaC;
    float gradient2 = luma2 - lumaC;

    bool is1Steepest = abs(gradient1) >= abs(gradient2);
    float gradientScaled = 0.25 * max(abs(gradient1), abs(gradient2));

    // Half-texel step onto the edge, towards the steeper side.
    float stepLength = isHorizontal ? t.y : t.x;
    float lumaLocalAverage;
    if (is1Steepest)
    {
        stepLength = -stepLength;
        lumaLocalAverage = 0.5 * (luma1 + lumaC);
    }
    else
    {
        lumaLocalAverage = 0.5 * (luma2 + lumaC);
    }

    float2 currentUv = uv;
    if (isHorizontal) currentUv.y += stepLength * 0.5;
    else              currentUv.x += stepLength * 0.5;

    // Walk outwards in both directions until the luma stops matching the edge.
    float2 offset = isHorizontal ? float2(t.x, 0.0) : float2(0.0, t.y);
    float2 uv1 = currentUv - offset;
    float2 uv2 = currentUv + offset;

    float lumaEnd1 = FxaaLuma(SampleSrc(uv1)) - lumaLocalAverage;
    float lumaEnd2 = FxaaLuma(SampleSrc(uv2)) - lumaLocalAverage;
    bool reached1 = abs(lumaEnd1) >= gradientScaled;
    bool reached2 = abs(lumaEnd2) >= gradientScaled;

    if (!reached1) uv1 -= offset;
    if (!reached2) uv2 += offset;

    if (!(reached1 && reached2))
    {
        [loop]
        for (int i = 2; i < FXAA_SEARCH_STEPS; i++)
        {
            if (!reached1)
            {
                lumaEnd1 = FxaaLuma(SampleSrc(uv1)) - lumaLocalAverage;
                reached1 = abs(lumaEnd1) >= gradientScaled;
            }
            if (!reached2)
            {
                lumaEnd2 = FxaaLuma(SampleSrc(uv2)) - lumaLocalAverage;
                reached2 = abs(lumaEnd2) >= gradientScaled;
            }

            if (!reached1) uv1 -= offset * FXAA_STEP[i];
            if (!reached2) uv2 += offset * FXAA_STEP[i];

            if (reached1 && reached2) break;
        }
    }

    float distance1 = isHorizontal ? (uv.x - uv1.x) : (uv.y - uv1.y);
    float distance2 = isHorizontal ? (uv2.x - uv.x) : (uv2.y - uv.y);

    bool isDirection1 = distance1 < distance2;
    float distanceFinal = min(distance1, distance2);
    float edgeThickness = distance1 + distance2;

    // Closer to an end of the edge => smaller shift.
    float pixelOffset = -distanceFinal / max(edgeThickness, 1e-5) + 0.5;

    // Reject the shift if the nearer end sits on the same side of the local
    // average as the centre - that means we walked off the edge, not along it.
    bool isLumaCenterSmaller = lumaC < lumaLocalAverage;
    bool correctVariation = ((isDirection1 ? lumaEnd1 : lumaEnd2) < 0.0) != isLumaCenterSmaller;
    float finalOffset = correctVariation ? pixelOffset : 0.0;

    // Sub-pixel term: catches single-pixel features the edge walk misses,
    // e.g. thin wires and distant Roblox part edges that shimmer when moving.
    float lumaAverage = (1.0 / 12.0) * (2.0 * (lumaDU + lumaLR) + lumaLCorners + lumaRCorners);
    float subPix1 = saturate(abs(lumaAverage - lumaC) / max(lumaRange, 1e-5));
    float subPix2 = (-2.0 * subPix1 + 3.0) * subPix1 * subPix1;
    float subPixFinal = subPix2 * subPix2 * FXAA_SUBPIX_QUALITY;

    finalOffset = max(finalOffset, subPixFinal);

    float2 finalUv = uv;
    if (isHorizontal) finalUv.y += finalOffset * stepLength;
    else              finalUv.x += finalOffset * stepLength;

    float3 antialiased = SampleSrc(finalUv);
    return float4(lerp(colorCenter, antialiased, strength), 1.0);
}
