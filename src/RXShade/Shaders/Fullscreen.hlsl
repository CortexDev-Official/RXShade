// Fullscreen triangle generated entirely from SV_VertexID.
//
// Drawn as Draw(3, 0) with TriangleList and no bound vertex/index buffers.
// A single oversized triangle beats two triangles for a fullscreen pass:
// no diagonal seam, and the rasteriser gets one primitive instead of two.
//
//   id 0 -> uv (0,0)  pos (-1,  1)   top-left
//   id 1 -> uv (2,0)  pos ( 3,  1)   off-screen right
//   id 2 -> uv (0,2)  pos (-1, -3)   off-screen bottom

struct VSOutput
{
    float4 position : SV_Position;
    float2 uv       : TEXCOORD0;
};

VSOutput VSMain(uint vertexId : SV_VertexID)
{
    VSOutput output;
    output.uv = float2((vertexId << 1) & 2, vertexId & 2);
    output.position = float4(output.uv.x * 2.0 - 1.0, 1.0 - output.uv.y * 2.0, 0.0, 1.0);
    return output;
}
