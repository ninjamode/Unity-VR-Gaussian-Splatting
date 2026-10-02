// SPDX-License-Identifier: MIT
#ifndef GAUSSIAN_SPLATTING_HLSL
#define GAUSSIAN_SPLATTING_HLSL

float SplatQuadHalfExtent(float opacity, float alphaCutoff, uint opacityAwareBounds)
{
    const float fullExtent = 2.0;
    // Negative opacity is the editor-selection sentinel. Its outline and
    // added opacity do not follow the ordinary Gaussian cutoff.
    if (opacityAwareBounds == 0u || alphaCutoff <= 0.0 || opacity < 0.0 ||
        !isfinite(opacity) || !isfinite(alphaCutoff))
        return fullExtent;

    // Fragment alpha uses half precision. Expand the analytic support
    // slightly to allow for rounding of exp() and the opacity product,
    // including half subnormals. The fragment cutoff remains authoritative.
    float cutoff = max(0.0, alphaCutoff * 0.998 - 5.96046448e-8);
    if (cutoff <= 0.0)
        return fullExtent;
    if (opacity <= cutoff)
        return 0.0;
    // Avoid log/sqrt for splats whose support already reaches the quad edge.
    if (opacity * 0.01831563889 >= cutoff) // exp(-fullExtent * fullExtent)
        return fullExtent;
    return min(fullExtent, sqrt(max(0.0, log(opacity / cutoff))));
}

float InvSquareCentered01(float x)
{
    x -= 0.5;
    x *= 0.5;
    x = sqrt(abs(x)) * sign(x);
    return x + 0.5;
}

float3 QuatRotateVector(float3 v, float4 r)
{
    float3 t = 2 * cross(r.xyz, v);
    return v + r.w * t + cross(r.xyz, t);
}

float4 QuatMul(float4 a, float4 b)
{
    return float4(a.wwww * b + (a.xyzx * b.wwwx + a.yzxy * b.zxyy) * float4(1,1,1,-1) - a.zxyz * b.yzxz);
}

float4 QuatInverse(float4 q)
{
    return rcp(dot(q, q)) * q * float4(-1,-1,-1,1);
}

float3x3 CalcMatrixFromRotationScale(float4 rot, float3 scale)
{
    float3x3 ms = float3x3(
        scale.x, 0, 0,
        0, scale.y, 0,
        0, 0, scale.z
    );
    float x = rot.x;
    float y = rot.y;
    float z = rot.z;
    float w = rot.w;
    float3x3 mr = float3x3(
        1-2*(y*y + z*z),   2*(x*y - w*z),   2*(x*z + w*y),
          2*(x*y + w*z), 1-2*(x*x + z*z),   2*(y*z - w*x),
          2*(x*z - w*y),   2*(y*z + w*x), 1-2*(x*x + y*y)
    );
    return mul(mr, ms);
}

void CalcCovariance3D(float3x3 rotMat, out float3 sigma0, out float3 sigma1)
{
    float3x3 sig = mul(rotMat, transpose(rotMat));
    sigma0 = float3(sig._m00, sig._m01, sig._m02);
    sigma1 = float3(sig._m11, sig._m12, sig._m22);
}

// from "EWA Splatting" (Zwicker et al 2002) eq. 31
float3 CalcCovariance2D(float3 worldPos, float3 cov3d0, float3 cov3d1, float4x4 matrixV, float4x4 matrixP, float4 screenParams)
{
    float4 viewPos = mul(matrixV, float4(worldPos, 1));
    float4 clip = mul(matrixP, viewPos);
    float2 ndc = clamp(clip.xy / clip.w, -1.3, 1.3);
    float3 jx = (matrixP[0].xyz - ndc.x * matrixP[3].xyz) / clip.w * screenParams.x * 0.5;
    float3 jy = (matrixP[1].xyz - ndc.y * matrixP[3].xyz) / clip.w * screenParams.y * 0.5;
    float3x3 J = float3x3(jx, jy, float3(0, 0, 0));
    float3x3 T = mul(J, (float3x3)matrixV);
    float3x3 V = float3x3(
        cov3d0.x, cov3d0.y, cov3d0.z,
        cov3d0.y, cov3d1.x, cov3d1.y,
        cov3d0.z, cov3d1.y, cov3d1.z);
    float3x3 cov = mul(T, mul(V, transpose(T)));

    // Low pass filter to make each splat at least 1px size.
    cov._m00 += 0.3;
    cov._m11 += 0.3;
    return float3(cov._m00, cov._m01, cov._m11);
}

float3 CalcConic(float3 cov2d)
{
    float det = cov2d.x * cov2d.z - cov2d.y * cov2d.y;
    return float3(cov2d.z, -cov2d.y, cov2d.x) * rcp(det);
}

float2 CalcScreenSpaceDelta(float2 svPositionXY, float2 centerXY, float4 projectionParams)
{
    float2 d = svPositionXY - centerXY;
    d.y *= projectionParams.x;
    return d;
}

float CalcPowerFromConic(float3 conic, float2 d)
{
    return -0.5 * (conic.x * d.x*d.x + conic.z * d.y*d.y) + conic.y * d.x*d.y;
}

// Morton interleaving 16x16 group i.e. by 4 bits of coordinates, based on this thread:
// https://twitter.com/rygorous/status/986715358852608000
// which is simplified version of https://fgiesen.wordpress.com/2009/12/13/decoding-morton-codes/
uint EncodeMorton2D_16x16(uint2 c)
{
    uint t = ((c.y & 0xF) << 8) | (c.x & 0xF); // ----EFGH----ABCD
    t = (t ^ (t << 2)) & 0x3333;               // --EF--GH--AB--CD
    t = (t ^ (t << 1)) & 0x5555;               // -E-F-G-H-A-B-C-D
    return (t | (t >> 7)) & 0xFF;              // --------EAFBGCHD
}
uint2 DecodeMorton2D_16x16(uint t)      // --------EAFBGCHD
{
    t = (t & 0xFF) | ((t & 0xFE) << 7); // -EAFBGCHEAFBGCHD
    t &= 0x5555;                        // -E-F-G-H-A-B-C-D
    t = (t ^ (t >> 1)) & 0x3333;        // --EF--GH--AB--CD
    t = (t ^ (t >> 2)) & 0x0f0f;        // ----EFGH----ABCD
    return uint2(t & 0xF, t >> 8);      // --------EFGHABCD
}


static const float SH_C1 = 0.4886025;
static const float SH_C2[] = { 1.0925484, -1.0925484, 0.3153916, -1.0925484, 0.5462742 };
static const float SH_C3[] = { -0.5900436, 2.8906114, -0.4570458, 0.3731763, -0.4570458, 1.4453057, -0.5900436 };

struct SplatSHData
{
    half3 col, sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, sh10, sh11, sh12, sh13, sh14, sh15;
};

half3 ShadeSH(SplatSHData splat, half3 dir, int shOrder, bool onlySH)
{
    dir *= -1;

    half x = dir.x, y = dir.y, z = dir.z;

    // ambient band
    half3 res = splat.col; // col = sh0 * SH_C0 + 0.5 is already precomputed
    if (onlySH)
        res = 0.5;
    // 1st degree
    if (shOrder >= 1)
    {
        res += SH_C1 * (-splat.sh1 * y + splat.sh2 * z - splat.sh3 * x);
        // 2nd degree
        if (shOrder >= 2)
        {
            half xx = x * x, yy = y * y, zz = z * z;
            half xy = x * y, yz = y * z, xz = x * z;
            res +=
                (SH_C2[0] * xy) * splat.sh4 +
                (SH_C2[1] * yz) * splat.sh5 +
                (SH_C2[2] * (2 * zz - xx - yy)) * splat.sh6 +
                (SH_C2[3] * xz) * splat.sh7 +
                (SH_C2[4] * (xx - yy)) * splat.sh8;
            // 3rd degree
            if (shOrder >= 3)
            {
                res +=
                    (SH_C3[0] * y * (3 * xx - yy)) * splat.sh9 +
                    (SH_C3[1] * xy * z) * splat.sh10 +
                    (SH_C3[2] * y * (4 * zz - xx - yy)) * splat.sh11 +
                    (SH_C3[3] * z * (2 * zz - 3 * xx - 3 * yy)) * splat.sh12 +
                    (SH_C3[4] * x * (4 * zz - xx - yy)) * splat.sh13 +
                    (SH_C3[5] * z * (xx - yy)) * splat.sh14 +
                    (SH_C3[6] * x * (xx - 3 * yy)) * splat.sh15;
            }
        }
    }
    return max(res, 0);
}

static const uint kTexWidth = 2048;

uint3 SplatIndexToPixelIndex(uint idx)
{
    uint3 res;

    uint2 xy = DecodeMorton2D_16x16(idx);
    uint width = kTexWidth / 16;
    idx >>= 8;
    res.x = (idx % width) * 16 + xy.x;
    res.y = (idx / width) * 16 + xy.y;
    res.z = 0;
    return res;
}

struct SplatChunkInfo
{
    uint colR, colG, colB, colA;
    float2 posX, posY, posZ;
    uint sclX, sclY, sclZ;
    uint shR, shG, shB;
};

StructuredBuffer<SplatChunkInfo> _SplatChunks;
uint _SplatChunkCount;

static const uint kChunkSize = 256;

struct SplatData
{
    float3 pos;
    float4 rot;
    float3 scale;
    half opacity;
    SplatSHData sh;
};

// Decode quaternion from a "smallest 3" e.g. 10.10.10.2 format
float4 DecodeRotation(float4 pq)
{
    uint idx = (uint)round(pq.w * 3.0); // note: need to round or index might come out wrong in some formats (e.g. fp16.fp16.fp16.fp16)
    float4 q;
    q.xyz = pq.xyz * sqrt(2.0) - (1.0 / sqrt(2.0));
    q.w = sqrt(1.0 - saturate(dot(q.xyz, q.xyz)));
    if (idx == 0) q = q.wxyz;
    if (idx == 1) q = q.xwyz;
    if (idx == 2) q = q.xywz;
    return q;
}
float4 PackSmallest3Rotation(float4 q)
{
    // find biggest component
    float4 absQ = abs(q);
    int index = 0;
    float maxV = absQ.x;
    if (absQ.y > maxV)
    {
        index = 1;
        maxV = absQ.y;
    }
    if (absQ.z > maxV)
    {
        index = 2;
        maxV = absQ.z;
    }
    if (absQ.w > maxV)
    {
        index = 3;
        maxV = absQ.w;
    }

    if (index == 0) q = q.yzwx;
    if (index == 1) q = q.xzwy;
    if (index == 2) q = q.xywz;

    float3 three = q.xyz * (q.w >= 0 ? 1 : -1); // -1/sqrt2..+1/sqrt2 range
    three = (three * sqrt(2.0)) * 0.5 + 0.5; // 0..1 range
    return float4(three, index / 3.0);
}

half3 DecodePacked_6_5_5(uint enc)
{
    return half3(
        (enc & 63) / 63.0,
        ((enc >> 6) & 31) / 31.0,
        ((enc >> 11) & 31) / 31.0);
}

half3 DecodePacked_5_6_5(uint enc)
{
    return half3(
        (enc & 31) / 31.0,
        ((enc >> 5) & 63) / 63.0,
        ((enc >> 11) & 31) / 31.0);
}

half3 DecodePacked_11_10_11(uint enc)
{
    return half3(
        (enc & 2047) / 2047.0,
        ((enc >> 11) & 1023) / 1023.0,
        ((enc >> 21) & 2047) / 2047.0);
}

float3 DecodePacked_16_16_16(uint2 enc)
{
    return float3(
        (enc.x & 65535) / 65535.0,
        ((enc.x >> 16) & 65535) / 65535.0,
        (enc.y & 65535) / 65535.0);
}

float4 DecodePacked_10_10_10_2(uint enc)
{
    return float4(
        (enc & 1023) / 1023.0,
        ((enc >> 10) & 1023) / 1023.0,
        ((enc >> 20) & 1023) / 1023.0,
        ((enc >> 30) & 3) / 3.0);
}
uint EncodeQuatToNorm10(float4 v) // 32 bits: 10.10.10.2
{
    return (uint) (v.x * 1023.5f) | ((uint) (v.y * 1023.5f) << 10) | ((uint) (v.z * 1023.5f) << 20) | ((uint) (v.w * 3.5f) << 30);
}


#ifdef SHADER_STAGE_COMPUTE
#define SplatBufferDataType RWByteAddressBuffer
#else
#define SplatBufferDataType ByteAddressBuffer
#endif

SplatBufferDataType _SplatPos;
SplatBufferDataType _SplatOther;
SplatBufferDataType _SplatSH;
Texture2D _SplatColor;
uint _SplatFormat;
struct GaussianFloatAttributes { float4 positionOpacity; float4 scale; float4 rotation; };
StructuredBuffer<GaussianFloatAttributes> _SplatFloatAttributes;
StructuredBuffer<uint> _SplatFloatSH;
uint _SplatSHStorage;
uint _SplatFloatSource, _SplatCoefficientCount;
float LoadSourceSHComponent(uint index, uint component)
{
    if (_SplatSHStorage == 0) return asfloat(_SplatFloatSH[index * _SplatCoefficientCount * 3 + component]);
    uint word = _SplatFloatSH[index * ((_SplatCoefficientCount * 3 + 1) / 2) + component / 2];
    return f16tof32((word >> ((component & 1) * 16)) & 0xffff);
}
float3 LoadFloatSH(uint index, uint coefficient)
{
    if (coefficient >= _SplatCoefficientCount) return 0;
    uint p = coefficient * 3;
    return float3(LoadSourceSHComponent(index,p), LoadSourceSHComponent(index,p+1), LoadSourceSHComponent(index,p+2));
}

// Match GaussianSplatAsset.VectorFormat
#define VECTOR_FMT_32F 0
#define VECTOR_FMT_16 1
#define VECTOR_FMT_11 2
#define VECTOR_FMT_6 3

uint LoadUShort(SplatBufferDataType dataBuffer, uint addrU)
{
    uint addrA = addrU & ~0x3;
    uint val = dataBuffer.Load(addrA);
    if (addrU != addrA)
        val >>= 16;
    return val & 0xFFFF;
}

uint LoadUInt(SplatBufferDataType dataBuffer, uint addrU)
{
    uint addrA = addrU & ~0x3;
    uint val = dataBuffer.Load(addrA);
    if (addrU != addrA)
    {
        uint val1 = dataBuffer.Load(addrA + 4);
        val = (val >> 16) | ((val1 & 0xFFFF) << 16);
    }
    return val;
}

float3 LoadAndDecodeVector(SplatBufferDataType dataBuffer, uint addrU, uint fmt)
{
    uint addrA = addrU & ~0x3;

    uint val0 = dataBuffer.Load(addrA);

    float3 res = 0;
    if (fmt == VECTOR_FMT_32F)
    {
        uint val1 = dataBuffer.Load(addrA + 4);
        uint val2 = dataBuffer.Load(addrA + 8);
        if (addrU != addrA)
        {
            uint val3 = dataBuffer.Load(addrA + 12);
            val0 = (val0 >> 16) | ((val1 & 0xFFFF) << 16);
            val1 = (val1 >> 16) | ((val2 & 0xFFFF) << 16);
            val2 = (val2 >> 16) | ((val3 & 0xFFFF) << 16);
        }
        res = float3(asfloat(val0), asfloat(val1), asfloat(val2));
    }
    else if (fmt == VECTOR_FMT_16)
    {
        uint val1 = dataBuffer.Load(addrA + 4);
        if (addrU != addrA)
        {
            val0 = (val0 >> 16) | ((val1 & 0xFFFF) << 16);
            val1 >>= 16;
        }
        res = DecodePacked_16_16_16(uint2(val0, val1));
    }
    else if (fmt == VECTOR_FMT_11)
    {
        uint val1 = dataBuffer.Load(addrA + 4);
        if (addrU != addrA)
        {
            val0 = (val0 >> 16) | ((val1 & 0xFFFF) << 16);
        }
        res = DecodePacked_11_10_11(val0);
    }
    else if (fmt == VECTOR_FMT_6)
    {
        if (addrU != addrA)
            val0 >>= 16;
        res = DecodePacked_6_5_5(val0);
    }
    return res;
}

float3 LoadSplatPosValue(uint index)
{
    if (_SplatFloatSource != 0) return _SplatFloatAttributes[index].positionOpacity.xyz;
    uint fmt = _SplatFormat & 0xFF;
    uint stride = 0;
    if (fmt == VECTOR_FMT_32F)
        stride = 12;
    else if (fmt == VECTOR_FMT_16)
        stride = 6;
    else if (fmt == VECTOR_FMT_11)
        stride = 4;
    else if (fmt == VECTOR_FMT_6)
        stride = 2;
    return LoadAndDecodeVector(_SplatPos, index * stride, fmt);
}

float3 LoadSplatPos(uint idx)
{
    if (_SplatFloatSource != 0) return _SplatFloatAttributes[idx].positionOpacity.xyz;
    float3 pos = LoadSplatPosValue(idx);
    uint chunkIdx = idx / kChunkSize;
    if (chunkIdx < _SplatChunkCount)
    {
        SplatChunkInfo chunk = _SplatChunks[chunkIdx];
        float3 posMin = float3(chunk.posX.x, chunk.posY.x, chunk.posZ.x);
        float3 posMax = float3(chunk.posX.y, chunk.posY.y, chunk.posZ.y);
        pos = lerp(posMin, posMax, pos);
    }
    return pos;
}

half4 LoadSplatColTex(uint3 coord)
{
    return _SplatColor.Load(coord);
}

// Load only SH coefficients. Packed DC color was already decoded with opacity;
// float sources store DC alongside the other SH coefficients.
SplatSHData LoadSplatSH(uint idx, half3 baseColor)
{
    SplatSHData sh = (SplatSHData)0;
    sh.col = baseColor;
    if (_SplatFloatSource != 0)
    {
        sh.col = 0.5 + 0.2820947918 * LoadFloatSH(idx, 0);
        sh.sh1 = LoadFloatSH(idx, 1);
        sh.sh2 = LoadFloatSH(idx, 2);
        sh.sh3 = LoadFloatSH(idx, 3);
        sh.sh4 = LoadFloatSH(idx, 4);
        sh.sh5 = LoadFloatSH(idx, 5);
        sh.sh6 = LoadFloatSH(idx, 6);
        sh.sh7 = LoadFloatSH(idx, 7);
        sh.sh8 = LoadFloatSH(idx, 8);
        sh.sh9 = LoadFloatSH(idx, 9);
        sh.sh10 = LoadFloatSH(idx, 10);
        sh.sh11 = LoadFloatSH(idx, 11);
        sh.sh12 = LoadFloatSH(idx, 12);
        sh.sh13 = LoadFloatSH(idx, 13);
        sh.sh14 = LoadFloatSH(idx, 14);
        sh.sh15 = LoadFloatSH(idx, 15);
        return sh;
    }
    uint scaleFmt = (_SplatFormat >> 8) & 0xFF;
    uint shFormat = (_SplatFormat >> 16) & 0xFF;

    uint otherStride = 4; // rotation is 10.10.10.2
    if (scaleFmt == VECTOR_FMT_32F)
    otherStride += 12;
    else if (scaleFmt == VECTOR_FMT_16)
    otherStride += 6;
    else if (scaleFmt == VECTOR_FMT_11)
    otherStride += 4;
    else if (scaleFmt == VECTOR_FMT_6)
    otherStride += 2;
    if (shFormat > VECTOR_FMT_6)
    otherStride += 2;
    uint otherAddr = idx * otherStride;

    uint shStride = 0;
    if (shFormat == VECTOR_FMT_32F)
    shStride = 192; // 15*3 fp32, rounded up to multiple of 16
    else if (shFormat == VECTOR_FMT_16 || shFormat > VECTOR_FMT_6)
    shStride = 96; // 15*3 fp16, rounded up to multiple of 16
    else if (shFormat == VECTOR_FMT_11)
    shStride = 60; // 15x uint
    else if (shFormat == VECTOR_FMT_6)
    shStride = 32; // 15x ushort, rounded up to multiple of 4

    uint shIndex = idx;
    if (shFormat > VECTOR_FMT_6)
    shIndex = LoadUShort(_SplatOther, otherAddr + otherStride - 2);

    uint shOffset = shIndex * shStride;
    uint4 shRaw0 = _SplatSH.Load4(shOffset);
    uint4 shRaw1 = _SplatSH.Load4(shOffset + 16);
    if (shFormat == VECTOR_FMT_32F)
    {
        uint4 shRaw2 = _SplatSH.Load4(shOffset + 32);
        uint4 shRaw3 = _SplatSH.Load4(shOffset + 48);
        uint4 shRaw4 = _SplatSH.Load4(shOffset + 64);
        uint4 shRaw5 = _SplatSH.Load4(shOffset + 80);
        uint4 shRaw6 = _SplatSH.Load4(shOffset + 96);
        uint4 shRaw7 = _SplatSH.Load4(shOffset + 112);
        uint4 shRaw8 = _SplatSH.Load4(shOffset + 128);
        uint4 shRaw9 = _SplatSH.Load4(shOffset + 144);
        uint4 shRawA = _SplatSH.Load4(shOffset + 160);
        uint  shRawB = _SplatSH.Load(shOffset + 176);
        sh.sh1.r  = asfloat(shRaw0.x); sh.sh1.g =  asfloat(shRaw0.y); sh.sh1.b =  asfloat(shRaw0.z);
        sh.sh2.r  = asfloat(shRaw0.w); sh.sh2.g =  asfloat(shRaw1.x); sh.sh2.b =  asfloat(shRaw1.y);
        sh.sh3.r  = asfloat(shRaw1.z); sh.sh3.g =  asfloat(shRaw1.w); sh.sh3.b =  asfloat(shRaw2.x);
        sh.sh4.r  = asfloat(shRaw2.y); sh.sh4.g =  asfloat(shRaw2.z); sh.sh4.b =  asfloat(shRaw2.w);
        sh.sh5.r  = asfloat(shRaw3.x); sh.sh5.g =  asfloat(shRaw3.y); sh.sh5.b =  asfloat(shRaw3.z);
        sh.sh6.r  = asfloat(shRaw3.w); sh.sh6.g =  asfloat(shRaw4.x); sh.sh6.b =  asfloat(shRaw4.y);
        sh.sh7.r  = asfloat(shRaw4.z); sh.sh7.g =  asfloat(shRaw4.w); sh.sh7.b =  asfloat(shRaw5.x);
        sh.sh8.r  = asfloat(shRaw5.y); sh.sh8.g =  asfloat(shRaw5.z); sh.sh8.b =  asfloat(shRaw5.w);
        sh.sh9.r  = asfloat(shRaw6.x); sh.sh9.g =  asfloat(shRaw6.y); sh.sh9.b =  asfloat(shRaw6.z);
        sh.sh10.r = asfloat(shRaw6.w); sh.sh10.g = asfloat(shRaw7.x); sh.sh10.b = asfloat(shRaw7.y);
        sh.sh11.r = asfloat(shRaw7.z); sh.sh11.g = asfloat(shRaw7.w); sh.sh11.b = asfloat(shRaw8.x);
        sh.sh12.r = asfloat(shRaw8.y); sh.sh12.g = asfloat(shRaw8.z); sh.sh12.b = asfloat(shRaw8.w);
        sh.sh13.r = asfloat(shRaw9.x); sh.sh13.g = asfloat(shRaw9.y); sh.sh13.b = asfloat(shRaw9.z);
        sh.sh14.r = asfloat(shRaw9.w); sh.sh14.g = asfloat(shRawA.x); sh.sh14.b = asfloat(shRawA.y);
        sh.sh15.r = asfloat(shRawA.z); sh.sh15.g = asfloat(shRawA.w); sh.sh15.b = asfloat(shRawB);
    }
    else if (shFormat == VECTOR_FMT_16 || shFormat > VECTOR_FMT_6)
    {
        uint4 shRaw2 = _SplatSH.Load4(shOffset + 32);
        uint4 shRaw3 = _SplatSH.Load4(shOffset + 48);
        uint4 shRaw4 = _SplatSH.Load4(shOffset + 64);
        uint3 shRaw5 = _SplatSH.Load3(shOffset + 80);
        sh.sh1.r  = f16tof32(shRaw0.x      ); sh.sh1.g =  f16tof32(shRaw0.x >> 16); sh.sh1.b =  f16tof32(shRaw0.y      );
        sh.sh2.r  = f16tof32(shRaw0.y >> 16); sh.sh2.g =  f16tof32(shRaw0.z      ); sh.sh2.b =  f16tof32(shRaw0.z >> 16);
        sh.sh3.r  = f16tof32(shRaw0.w      ); sh.sh3.g =  f16tof32(shRaw0.w >> 16); sh.sh3.b =  f16tof32(shRaw1.x      );
        sh.sh4.r  = f16tof32(shRaw1.x >> 16); sh.sh4.g =  f16tof32(shRaw1.y      ); sh.sh4.b =  f16tof32(shRaw1.y >> 16);
        sh.sh5.r  = f16tof32(shRaw1.z      ); sh.sh5.g =  f16tof32(shRaw1.z >> 16); sh.sh5.b =  f16tof32(shRaw1.w      );
        sh.sh6.r  = f16tof32(shRaw1.w >> 16); sh.sh6.g =  f16tof32(shRaw2.x      ); sh.sh6.b =  f16tof32(shRaw2.x >> 16);
        sh.sh7.r  = f16tof32(shRaw2.y      ); sh.sh7.g =  f16tof32(shRaw2.y >> 16); sh.sh7.b =  f16tof32(shRaw2.z      );
        sh.sh8.r  = f16tof32(shRaw2.z >> 16); sh.sh8.g =  f16tof32(shRaw2.w      ); sh.sh8.b =  f16tof32(shRaw2.w >> 16);
        sh.sh9.r  = f16tof32(shRaw3.x      ); sh.sh9.g =  f16tof32(shRaw3.x >> 16); sh.sh9.b =  f16tof32(shRaw3.y      );
        sh.sh10.r = f16tof32(shRaw3.y >> 16); sh.sh10.g = f16tof32(shRaw3.z      ); sh.sh10.b = f16tof32(shRaw3.z >> 16);
        sh.sh11.r = f16tof32(shRaw3.w      ); sh.sh11.g = f16tof32(shRaw3.w >> 16); sh.sh11.b = f16tof32(shRaw4.x      );
        sh.sh12.r = f16tof32(shRaw4.x >> 16); sh.sh12.g = f16tof32(shRaw4.y      ); sh.sh12.b = f16tof32(shRaw4.y >> 16);
        sh.sh13.r = f16tof32(shRaw4.z      ); sh.sh13.g = f16tof32(shRaw4.z >> 16); sh.sh13.b = f16tof32(shRaw4.w      );
        sh.sh14.r = f16tof32(shRaw4.w >> 16); sh.sh14.g = f16tof32(shRaw5.x      ); sh.sh14.b = f16tof32(shRaw5.x >> 16);
        sh.sh15.r = f16tof32(shRaw5.y      ); sh.sh15.g = f16tof32(shRaw5.y >> 16); sh.sh15.b = f16tof32(shRaw5.z      );
    }
    else if (shFormat == VECTOR_FMT_11)
    {
        uint4 shRaw2 = _SplatSH.Load4(shOffset + 32);
        uint3 shRaw3 = _SplatSH.Load3(shOffset + 48);
        sh.sh1 =  DecodePacked_11_10_11(shRaw0.x);
        sh.sh2 =  DecodePacked_11_10_11(shRaw0.y);
        sh.sh3 =  DecodePacked_11_10_11(shRaw0.z);
        sh.sh4 =  DecodePacked_11_10_11(shRaw0.w);
        sh.sh5 =  DecodePacked_11_10_11(shRaw1.x);
        sh.sh6 =  DecodePacked_11_10_11(shRaw1.y);
        sh.sh7 =  DecodePacked_11_10_11(shRaw1.z);
        sh.sh8 =  DecodePacked_11_10_11(shRaw1.w);
        sh.sh9 =  DecodePacked_11_10_11(shRaw2.x);
        sh.sh10 = DecodePacked_11_10_11(shRaw2.y);
        sh.sh11 = DecodePacked_11_10_11(shRaw2.z);
        sh.sh12 = DecodePacked_11_10_11(shRaw2.w);
        sh.sh13 = DecodePacked_11_10_11(shRaw3.x);
        sh.sh14 = DecodePacked_11_10_11(shRaw3.y);
        sh.sh15 = DecodePacked_11_10_11(shRaw3.z);
    }
    else if (shFormat == VECTOR_FMT_6)
    {
        sh.sh1 =  DecodePacked_5_6_5(shRaw0.x);
        sh.sh2 =  DecodePacked_5_6_5(shRaw0.x >> 16);
        sh.sh3 =  DecodePacked_5_6_5(shRaw0.y);
        sh.sh4 =  DecodePacked_5_6_5(shRaw0.y >> 16);
        sh.sh5 =  DecodePacked_5_6_5(shRaw0.z);
        sh.sh6 =  DecodePacked_5_6_5(shRaw0.z >> 16);
        sh.sh7 =  DecodePacked_5_6_5(shRaw0.w);
        sh.sh8 =  DecodePacked_5_6_5(shRaw0.w >> 16);
        sh.sh9 =  DecodePacked_5_6_5(shRaw1.x);
        sh.sh10 = DecodePacked_5_6_5(shRaw1.x >> 16);
        sh.sh11 = DecodePacked_5_6_5(shRaw1.y);
        sh.sh12 = DecodePacked_5_6_5(shRaw1.y >> 16);
        sh.sh13 = DecodePacked_5_6_5(shRaw1.z);
        sh.sh14 = DecodePacked_5_6_5(shRaw1.z >> 16);
        sh.sh15 = DecodePacked_5_6_5(shRaw1.w);
    }

    uint chunkIdx = idx / kChunkSize;
    if (chunkIdx < _SplatChunkCount && shFormat > VECTOR_FMT_32F && shFormat <= VECTOR_FMT_6)
    {
        SplatChunkInfo chunk = _SplatChunks[chunkIdx];
        half3 shMin = half3(f16tof32(chunk.shR), f16tof32(chunk.shG), f16tof32(chunk.shB));
        half3 shMax = half3(f16tof32(chunk.shR >> 16), f16tof32(chunk.shG >> 16), f16tof32(chunk.shB >> 16));
        sh.sh1    = lerp(shMin, shMax, sh.sh1 );
        sh.sh2    = lerp(shMin, shMax, sh.sh2 );
        sh.sh3    = lerp(shMin, shMax, sh.sh3 );
        sh.sh4    = lerp(shMin, shMax, sh.sh4 );
        sh.sh5    = lerp(shMin, shMax, sh.sh5 );
        sh.sh6    = lerp(shMin, shMax, sh.sh6 );
        sh.sh7    = lerp(shMin, shMax, sh.sh7 );
        sh.sh8    = lerp(shMin, shMax, sh.sh8 );
        sh.sh9    = lerp(shMin, shMax, sh.sh9 );
        sh.sh10   = lerp(shMin, shMax, sh.sh10);
        sh.sh11   = lerp(shMin, shMax, sh.sh11);
        sh.sh12   = lerp(shMin, shMax, sh.sh12);
        sh.sh13   = lerp(shMin, shMax, sh.sh13);
        sh.sh14   = lerp(shMin, shMax, sh.sh14);
        sh.sh15   = lerp(shMin, shMax, sh.sh15);
    }
    return sh;
}

SplatData LoadSplatData(uint idx, bool loadSH)
{
    SplatData s = (SplatData)0;

    if (_SplatFloatSource != 0)
    {
        GaussianFloatAttributes value = _SplatFloatAttributes[idx];
        s.pos = value.positionOpacity.xyz;
        s.opacity = value.positionOpacity.w;
        s.scale = value.scale.xyz;
        s.rot = value.rotation;
        if (loadSH) s.sh = LoadSplatSH(idx, 0);
        return s;
    }

    // figure out raw data offsets / locations
    uint3 coord = SplatIndexToPixelIndex(idx);

    uint scaleFmt = (_SplatFormat >> 8) & 0xFF;
    uint shFormat = (_SplatFormat >> 16) & 0xFF;

    uint otherStride = 4; // rotation is 10.10.10.2
    if (scaleFmt == VECTOR_FMT_32F)
        otherStride += 12;
    else if (scaleFmt == VECTOR_FMT_16)
        otherStride += 6;
    else if (scaleFmt == VECTOR_FMT_11)
        otherStride += 4;
    else if (scaleFmt == VECTOR_FMT_6)
        otherStride += 2;
    if (shFormat > VECTOR_FMT_6)
        otherStride += 2;
    uint otherAddr = idx * otherStride;

    // load raw splat data, which might be chunk-relative
    s.pos       = LoadSplatPosValue(idx);
    s.rot       = DecodeRotation(DecodePacked_10_10_10_2(LoadUInt(_SplatOther, otherAddr)));
    s.scale     = LoadAndDecodeVector(_SplatOther, otherAddr + 4, scaleFmt);
    half4 col   = LoadSplatColTex(coord);

    // if raw data is chunk-relative, convert to final values by interpolating between chunk min/max
    uint chunkIdx = idx / kChunkSize;
    if (chunkIdx < _SplatChunkCount)
    {
        SplatChunkInfo chunk = _SplatChunks[chunkIdx];
        float3 posMin = float3(chunk.posX.x, chunk.posY.x, chunk.posZ.x);
        float3 posMax = float3(chunk.posX.y, chunk.posY.y, chunk.posZ.y);
        half3 sclMin = half3(f16tof32(chunk.sclX    ), f16tof32(chunk.sclY    ), f16tof32(chunk.sclZ    ));
        half3 sclMax = half3(f16tof32(chunk.sclX>>16), f16tof32(chunk.sclY>>16), f16tof32(chunk.sclZ>>16));
        half4 colMin = half4(f16tof32(chunk.colR    ), f16tof32(chunk.colG    ), f16tof32(chunk.colB    ), f16tof32(chunk.colA    ));
        half4 colMax = half4(f16tof32(chunk.colR>>16), f16tof32(chunk.colG>>16), f16tof32(chunk.colB>>16), f16tof32(chunk.colA>>16));
        s.pos = lerp(posMin, posMax, s.pos);
        s.scale     = lerp(sclMin, sclMax, s.scale);
        s.scale *= s.scale;
        s.scale *= s.scale;
        s.scale *= s.scale;
        col   = lerp(colMin, colMax, col);
        col.a = InvSquareCentered01(col.a);
    }
    s.opacity   = col.a;
    s.sh.col    = col.rgb;
    if (loadSH) s.sh = LoadSplatSH(idx, s.sh.col);

    return s;
}

SplatData LoadSplatData(uint idx)
{
    return LoadSplatData(idx, true);
}

struct SplatViewData
{
    float4 pos;
    float2 axis1, axis2;
    uint2 color; // 4xFP16
};

// If we are rendering into backbuffer directly (e.g. HDR off, no postprocessing),
// the color target texture is a render target (so projection is upside down),
// but the depth buffer we get is not upside down. We want to flip
// our rendering upside down manually for this case.
//
// There does not seem to be a good way to detect this situation in Unity; work around it
// by setting _CameraTargetTexture global texture to BuiltinRenderTextureType.CameraTarget
// from the command buffer. When CameraTarget is null (the backbuffer), its _TexelSize
// property of the texture will get set to (1,1,1,1).
float4 _CameraTargetTexture_TexelSize;
void FlipProjectionIfBackbuffer(inout float4 vpos)
{
    if (_CameraTargetTexture_TexelSize.z == 1.0)
        vpos.y = -vpos.y;
}

#endif // GAUSSIAN_SPLATTING_HLSL
