// Straight copy. Two jobs:
//
//  1. "Ingest" - crops the Windows.Graphics.Capture pool texture down to the
//     live content region (UvScale) and converts BGRA8 -> the float16 working
//     format the filter chain runs in.
//
//  2. "Present" - blits the finished frame into a swap chain back buffer.
//
// With every filter switched off, the passthrough path is exactly ONE of
// these per output surface. That is the zero-filter baseline used to prove
// the capture pipeline hits full framerate before any effect is added.

float4 PSMain(PSInput input) : SV_Target
{
    return float4(SampleSrc(input.uv * UvScale + UvOffset), 1.0);
}
