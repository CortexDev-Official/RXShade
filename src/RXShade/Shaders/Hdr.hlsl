// HDR tone mapping and local contrast.
//
// Params0 = (exposure, localContrast, highlightRecovery, shadowLift)
// Params1 = (vibrance, radius, 0, 0)
//
// WHAT "HDR" ACTUALLY MEANS HERE:
// The captured frame is 8-bit SDR - the dynamic range is already gone. What
// this does is the *tone mapping half* of an HDR pipeline, which is what
// people are reacting to when they say a shader mod "looks HDR":
//
//  1. ACES filmic tone curve, applied in LINEAR light. Rolls highlights off
//     smoothly instead of clipping them flat and deepens shadows.
//  2. Local contrast (unsharp mask). The real "HDR photo" signature - it lifts
//     detail against its surroundings rather than globally.
//  3. Highlight recovery and shadow lift so both ends stay readable.
//  4. Vibrance - saturation weighted towards already-dull colours.
//
// TWO THINGS THAT MUST BE RIGHT, OR THIS WASHES THE IMAGE OUT:
//
//  * ACES is defined on scene-referred LINEAR light. The captured frame is
//    display-encoded sRGB. Feeding sRGB values straight into the curve is
//    simply wrong and greys out every bright area - which is exactly what it
//    did on the Roblox menu.
//
//  * The curve must be normalised by its own white point. Raw ACES maps 1.0 to
//    0.80, so pure white comes out as light grey. Dividing by ACES(1.0) puts
//    white back at white while keeping the shoulder shape.

static const float2 kDisc[8] =
{
    float2( 1.0,  0.0), float2(-1.0,  0.0), float2( 0.0,  1.0), float2( 0.0, -1.0),
    float2( 0.7,  0.7), float2(-0.7,  0.7), float2( 0.7, -0.7), float2(-0.7, -0.7)
};

// Pre-scale chosen so that with the exposure slider centred the round trip
// through the curve is very close to identity: an untouched mid-grey comes back
// as mid-grey rather than a stop brighter.
#define ACES_PRE_SCALE 0.60

// Narkowicz's ACES approximation. Cheap, and indistinguishable from the
// reference curve at 8-bit output.
float3 AcesFilmic(float3 x)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return (x * (a * x + b)) / (x * (c * x + d) + e);
}

float3 SrgbToLinear(float3 c) { return pow(max(c, 0.0), 2.2); }
float3 LinearToSrgb(float3 c) { return pow(max(c, 0.0), 1.0 / 2.2); }

float4 PSMain(PSInput input) : SV_Target
{
    float2 uv = input.uv;
    float3 c = SampleSrc(uv);

    float exposure          = Params0.x;
    float localContrast     = Params0.y;
    float highlightRecovery = Params0.z;
    float shadowLift        = Params0.w;

    float vibrance = Params1.x;
    float radius   = Params1.y;

    // ---- Into linear light ---------------------------------------------
    float3 linearColor = SrgbToLinear(c) * exposure * ACES_PRE_SCALE;

    // ---- Tonal shaping, still linear ------------------------------------
    float linearLuma = Luma(linearColor);

    // Pull the brightest areas down before the curve sees them, so they keep
    // their shape instead of flattening into a single value.
    float highlightMask = smoothstep(0.25, 0.85, linearLuma);
    linearColor *= 1.0 - highlightMask * highlightRecovery * 0.45;

    // Lift shadows without crushing the blacks flat.
    float shadowMask = 1.0 - smoothstep(0.0, 0.18, linearLuma);
    linearColor += shadowMask * shadowLift * 0.045;

    // ---- ACES, normalised to its own white point ------------------------
    const float3 acesWhite = AcesFilmic(1.0.xxx);
    float3 toned = saturate(AcesFilmic(max(linearColor, 0.0)) / acesWhite);

    // ---- Back to display space ------------------------------------------
    c = LinearToSrgb(toned);

    // ---- Local contrast, in display space -------------------------------
    // Applied after tone mapping because this is a perceptual "clarity" move,
    // not a light-transport one, and it is far more predictable here.
    if (localContrast > 0.001)
    {
        float2 step = InvSourceSize * radius;

        float3 blurred = c;
        [unroll]
        for (int i = 0; i < 8; i++)
        {
            float3 tap = SampleSrc(uv + kDisc[i] * step);
            tap = SrgbToLinear(tap) * exposure * ACES_PRE_SCALE;
            blurred += LinearToSrgb(saturate(AcesFilmic(max(tap, 0.0)) / acesWhite));
        }
        blurred /= 9.0;

        float3 detail = c - blurred;

        // Soft-limit so hard edges (HUD text, UI outlines) do not grow halos,
        // which is the classic overcooked-HDR artefact.
        detail = sign(detail) * (1.0 - exp(-abs(detail) * 5.0)) * 0.22;

        c += detail * localContrast * 3.5;
    }

    // ---- Vibrance --------------------------------------------------------
    if (vibrance > 0.001)
    {
        float gray = Luma(c);
        float maxChannel = max(c.r, max(c.g, c.b));
        float minChannel = min(c.r, min(c.g, c.b));
        float currentSat = saturate(maxChannel - minChannel);

        // Weighted by how UNsaturated the pixel already is, so muted colours
        // gain and vivid ones are left alone - no neon skin tones.
        c = lerp(gray.xxx, c, 1.0 + vibrance * 0.9 * (1.0 - currentSat));
    }

    return float4(saturate(c), 1.0);
}
