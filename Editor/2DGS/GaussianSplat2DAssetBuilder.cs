// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.IO;
using System.Runtime.CompilerServices;
using Gaussians.TwoD.Editor.Utils;
using Gaussians.TwoD;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Gaussians.Core.Editor.Importing;
using static Gaussians.Core.Editor.Importing.GaussianImportIO;
using static Gaussians.Core.Editor.Importing.GaussianImportPacking;
using UnityEditor;
using UnityEngine;

namespace Gaussians.TwoD.Editor
{
    public readonly struct GaussianSplat2DImportSettings
    {
        public readonly GaussianSplat2DAsset.VectorFormat Position;
        public readonly GaussianSplat2DAsset.ScaleFormat Scale;
        public readonly GaussianSplat2DAsset.ColorFormat Color;
        public readonly GaussianSplat2DAsset.SHFormat SH;
        public GaussianSplat2DImportSettings(GaussianSplat2DAsset.VectorFormat position, GaussianSplat2DAsset.ScaleFormat scale, GaussianSplat2DAsset.ColorFormat color, GaussianSplat2DAsset.SHFormat sh)
        { Position = position; Scale = scale; Color = color; SH = sh; }
    }
    [BurstCompile]
    public sealed class GaussianSplat2DAssetBuilder
    {
        const string kProgressTitle = "Creating 2D Gaussian Splat Asset";
        readonly GaussianSplat2DAsset.VectorFormat m_FormatPos;
        readonly GaussianSplat2DAsset.ScaleFormat m_FormatScale;
        readonly GaussianSplat2DAsset.ColorFormat m_FormatColor;
        readonly GaussianSplat2DAsset.SHFormat m_FormatSH;
        bool isUsingChunks => m_FormatPos != GaussianSplat2DAsset.VectorFormat.Float32 || m_FormatScale != GaussianSplat2DAsset.ScaleFormat.Float32x2 || m_FormatColor != GaussianSplat2DAsset.ColorFormat.Float32x4 || m_FormatSH != GaussianSplat2DAsset.SHFormat.Float32;
        public GaussianSplat2DAssetBuilder(GaussianSplat2DImportSettings settings)
        { m_FormatPos = settings.Position; m_FormatScale = settings.Scale; m_FormatColor = settings.Color; m_FormatSH = settings.SH; }
        public unsafe GaussianSplat2DAsset Build(NativeArray<InputSplatData> inputSplats, string outputFolder, string baseName, GaussianSplat2DAsset.CameraInfo[] cameras)
        {
            if (inputSplats.Length == 0 || inputSplats.Length > GaussianSplat2DAsset.kMaxSplats) throw new IOException("Unsupported Gaussian asset count: " + inputSplats.Length);
            ValidateOutputFolder(outputFolder);
            Directory.CreateDirectory(outputFolder);
            NativeArray<int> splatSHIndices = default;
            NativeArray<byte> clusteredSHs = default;
            GaussianSplat2DAsset asset = null;
            try
            {
                float3 centerBoundsMin, centerBoundsMax, renderBoundsMin, renderBoundsMax;
                var boundsJob = new CalcBoundsJob
                {
                    m_CenterBoundsMin = &centerBoundsMin,
                    m_CenterBoundsMax = &centerBoundsMax,
                    m_RenderBoundsMin = &renderBoundsMin,
                    m_RenderBoundsMax = &renderBoundsMax,
                    m_SplatData = inputSplats
                };
                boundsJob.Schedule().Complete();

                EditorUtility.DisplayProgressBar(kProgressTitle, "Morton reordering", 0.05f);
                ReorderMorton(inputSplats, centerBoundsMin, centerBoundsMax);

                // cluster SHs
                if (m_FormatSH >= GaussianSplat2DAsset.SHFormat.Cluster64k)
                {
                    EditorUtility.DisplayProgressBar(kProgressTitle, "Cluster SHs", 0.2f);
                    ClusterSHs(inputSplats, m_FormatSH, out clusteredSHs, out splatSHIndices);
                }

                EditorUtility.DisplayProgressBar(kProgressTitle, "Creating data objects", 0.7f);
                asset = ScriptableObject.CreateInstance<GaussianSplat2DAsset>();
                asset.Initialize(inputSplats.Length, m_FormatPos, m_FormatScale, m_FormatColor, m_FormatSH,
                    renderBoundsMin, renderBoundsMax, cameras, true);
                asset.name = baseName;

                var dataHash = new Hash128((uint)asset.splatCount, (uint)asset.formatVersion, 0, 0);
                string pathChunk = $"{outputFolder}/{baseName}_chk.bytes";
                string pathPos = $"{outputFolder}/{baseName}_pos.bytes";
                string pathOther = $"{outputFolder}/{baseName}_oth.bytes";
                string pathCol = $"{outputFolder}/{baseName}_col.bytes";
                string pathSh = $"{outputFolder}/{baseName}_shs.bytes";

                // if we are using full lossless (FP32) data, then do not use any chunking, and keep data as-is
                bool useChunks = isUsingChunks;
                if (useChunks)
                    CreateChunkData(inputSplats, pathChunk, ref dataHash, m_FormatScale);
                CreatePositionsData(inputSplats, pathPos, ref dataHash);
                CreateOtherData(inputSplats, pathOther, ref dataHash, splatSHIndices);
                CreateColorData(inputSplats, pathCol, ref dataHash);
                CreateSHData(inputSplats, pathSh, ref dataHash, clusteredSHs);
                asset.SetDataHash(dataHash);

                // Import generated byte files before assigning their TextAsset references.
                EditorUtility.DisplayProgressBar(kProgressTitle, "Initial texture import", 0.85f);
                AssetDatabase.Refresh(ImportAssetOptions.ForceUncompressedImport);

                EditorUtility.DisplayProgressBar(kProgressTitle, "Setup data onto asset", 0.95f);
                asset.SetAssetFiles(
                    useChunks ? AssetDatabase.LoadAssetAtPath<TextAsset>(pathChunk) : null,
                    AssetDatabase.LoadAssetAtPath<TextAsset>(pathPos),
                    AssetDatabase.LoadAssetAtPath<TextAsset>(pathOther),
                    AssetDatabase.LoadAssetAtPath<TextAsset>(pathCol),
                    AssetDatabase.LoadAssetAtPath<TextAsset>(pathSh));

                var assetPath = $"{outputFolder}/{baseName}.asset";
                var savedAsset = CreateOrReplaceAsset(asset, assetPath);

                EditorUtility.DisplayProgressBar(kProgressTitle, "Saving assets", 0.99f);
                AssetDatabase.SaveAssets();

                return savedAsset;
            }
            finally
            {
                if (splatSHIndices.IsCreated) splatSHIndices.Dispose();
                if (clusteredSHs.IsCreated) clusteredSHs.Dispose();
                if (asset && !AssetDatabase.Contains(asset)) UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [BurstCompile]
        struct CalcBoundsJob : IJob
        {
            // The renderer exposes a maximum additional splat scale of two. Store bounds for
            // that maximum so direct-path culling remains conservative at every supported value.
            const float kThreeSigmaAtMaxRuntimeScale = 6.0f;

            [NativeDisableUnsafePtrRestriction] public unsafe float3* m_CenterBoundsMin;
            [NativeDisableUnsafePtrRestriction] public unsafe float3* m_CenterBoundsMax;
            [NativeDisableUnsafePtrRestriction] public unsafe float3* m_RenderBoundsMin;
            [NativeDisableUnsafePtrRestriction] public unsafe float3* m_RenderBoundsMax;
            [ReadOnly] public NativeArray<InputSplatData> m_SplatData;

            public unsafe void Execute()
            {
                float3 centerBoundsMin = float.PositiveInfinity;
                float3 centerBoundsMax = float.NegativeInfinity;
                float3 renderBoundsMin = float.PositiveInfinity;
                float3 renderBoundsMax = float.NegativeInfinity;

                for (int i = 0; i < m_SplatData.Length; ++i)
                {
                    InputSplatData splat = m_SplatData[i];
                    float3 pos = splat.pos;
                    centerBoundsMin = math.min(centerBoundsMin, pos);
                    centerBoundsMax = math.max(centerBoundsMax, pos);
                    EncapsulatePlanarSplat(splat, kThreeSigmaAtMaxRuntimeScale,
                        ref renderBoundsMin, ref renderBoundsMax);
                }
                *m_CenterBoundsMin = centerBoundsMin;
                *m_CenterBoundsMax = centerBoundsMax;
                *m_RenderBoundsMin = renderBoundsMin;
                *m_RenderBoundsMax = renderBoundsMax;
            }
        }

        internal static void EncapsulatePlanarSplat(InputSplatData splat, float sigmaScale,
            ref float3 boundsMin, ref float3 boundsMax)
        {
            float4 packedRotation = new(splat.rot.x, splat.rot.y, splat.rot.z, splat.rot.w);
            int largestIndex = (int)math.round(packedRotation.w * 3.0f);
            float3 smallest = packedRotation.xyz * math.SQRT2 - 1.0f / math.SQRT2;
            float largest = math.sqrt(math.max(0.0f, 1.0f - math.dot(smallest, smallest)));
            float4 decodedRotation = new(smallest, largest);
            if (largestIndex == 0) decodedRotation = decodedRotation.wxyz;
            if (largestIndex == 1) decodedRotation = decodedRotation.xwyz;
            if (largestIndex == 2) decodedRotation = decodedRotation.xywz;

            quaternion rotation = new(decodedRotation);
            float3 tangentU = math.rotate(rotation, new float3(splat.scale.x, 0.0f, 0.0f));
            float3 tangentV = math.rotate(rotation, new float3(0.0f, splat.scale.y, 0.0f));
            float3 footprintExtent = sigmaScale * (math.abs(tangentU) + math.abs(tangentV));
            float3 pos = splat.pos;
            boundsMin = math.min(boundsMin, pos - footprintExtent);
            boundsMax = math.max(boundsMax, pos + footprintExtent);
        }

        static void ReorderMorton(NativeArray<InputSplatData> records, float3 min, float3 max) => GaussianImportOrder.ReorderMorton(records, min, max, 0);

        static bool ClusterSHProgress(float val)
        {
            EditorUtility.DisplayProgressBar(kProgressTitle, $"Cluster SHs ({val:P0})", 0.2f + val * 0.5f);
            return true;
        }

        static void ClusterSHs(NativeArray<InputSplatData> records, GaussianSplat2DAsset.SHFormat format, out NativeArray<byte> table, out NativeArray<int> indices)
        {
            float passes = format switch
            {
                GaussianSplat2DAsset.SHFormat.Cluster64k => .3f, GaussianSplat2DAsset.SHFormat.Cluster32k => .4f,
                GaussianSplat2DAsset.SHFormat.Cluster16k => .5f, GaussianSplat2DAsset.SHFormat.Cluster8k => .8f,
                GaussianSplat2DAsset.SHFormat.Cluster4k => 1.2f, _ => throw new ArgumentOutOfRangeException(nameof(format))
            };
            GaussianImportSH.Cluster(records, 24, GaussianSplat2DAsset.GetSHCount(format, records.Length), passes, ClusterSHProgress, out table, out indices);
        }

        [BurstCompile]
        struct CalcChunkDataJob : IJobParallelFor
        {
            [NativeDisableParallelForRestriction] public NativeArray<InputSplatData> splatData;
            public NativeArray<GaussianSplat2DAsset.ChunkInfo> chunks;
            public int encodeScaleRelativeToChunk;

            public void Execute(int chunkIdx)
            {
                float3 chunkMinpos = float.PositiveInfinity;
                float2 chunkMinscl = float.PositiveInfinity;
                float4 chunkMincol = float.PositiveInfinity;
                float3 chunkMinshs = float.PositiveInfinity;
                float3 chunkMaxpos = float.NegativeInfinity;
                float2 chunkMaxscl = float.NegativeInfinity;
                float4 chunkMaxcol = float.NegativeInfinity;
                float3 chunkMaxshs = float.NegativeInfinity;

                int splatBegin = math.min(chunkIdx * GaussianSplat2DAsset.kChunkSize, splatData.Length);
                int splatEnd = math.min((chunkIdx + 1) * GaussianSplat2DAsset.kChunkSize, splatData.Length);

                // calculate data bounds inside the chunk
                for (int i = splatBegin; i < splatEnd; ++i)
                {
                    InputSplatData s = splatData[i];

                    // Compressed scales are encoded relative to their chunk. Float32x2
                    // stays in the imported linear-scale representation without quantization.
                    if (encodeScaleRelativeToChunk != 0)
                        s.scale = math.pow(s.scale, 1.0f / 8.0f);
                    // transform opacity to be more uniformly distributed
                    s.opacity = GaussianSplat2DUtils.SquareCentered01(s.opacity);
                    splatData[i] = s;

                    chunkMinpos = math.min(chunkMinpos, s.pos);
                    chunkMinscl = math.min(chunkMinscl, s.scale);
                    chunkMincol = math.min(chunkMincol, new float4(s.dc0, s.opacity));
                    chunkMinshs = math.min(chunkMinshs, s.sh1);
                    chunkMinshs = math.min(chunkMinshs, s.sh2);
                    chunkMinshs = math.min(chunkMinshs, s.sh3);
                    chunkMinshs = math.min(chunkMinshs, s.sh4);
                    chunkMinshs = math.min(chunkMinshs, s.sh5);
                    chunkMinshs = math.min(chunkMinshs, s.sh6);
                    chunkMinshs = math.min(chunkMinshs, s.sh7);
                    chunkMinshs = math.min(chunkMinshs, s.sh8);
                    chunkMinshs = math.min(chunkMinshs, s.sh9);
                    chunkMinshs = math.min(chunkMinshs, s.shA);
                    chunkMinshs = math.min(chunkMinshs, s.shB);
                    chunkMinshs = math.min(chunkMinshs, s.shC);
                    chunkMinshs = math.min(chunkMinshs, s.shD);
                    chunkMinshs = math.min(chunkMinshs, s.shE);
                    chunkMinshs = math.min(chunkMinshs, s.shF);

                    chunkMaxpos = math.max(chunkMaxpos, s.pos);
                    chunkMaxscl = math.max(chunkMaxscl, s.scale);
                    chunkMaxcol = math.max(chunkMaxcol, new float4(s.dc0, s.opacity));
                    chunkMaxshs = math.max(chunkMaxshs, s.sh1);
                    chunkMaxshs = math.max(chunkMaxshs, s.sh2);
                    chunkMaxshs = math.max(chunkMaxshs, s.sh3);
                    chunkMaxshs = math.max(chunkMaxshs, s.sh4);
                    chunkMaxshs = math.max(chunkMaxshs, s.sh5);
                    chunkMaxshs = math.max(chunkMaxshs, s.sh6);
                    chunkMaxshs = math.max(chunkMaxshs, s.sh7);
                    chunkMaxshs = math.max(chunkMaxshs, s.sh8);
                    chunkMaxshs = math.max(chunkMaxshs, s.sh9);
                    chunkMaxshs = math.max(chunkMaxshs, s.shA);
                    chunkMaxshs = math.max(chunkMaxshs, s.shB);
                    chunkMaxshs = math.max(chunkMaxshs, s.shC);
                    chunkMaxshs = math.max(chunkMaxshs, s.shD);
                    chunkMaxshs = math.max(chunkMaxshs, s.shE);
                    chunkMaxshs = math.max(chunkMaxshs, s.shF);
                }

                // make sure bounds are not zero
                chunkMaxpos = math.max(chunkMaxpos, chunkMinpos + 1.0e-5f);
                chunkMaxscl = math.max(chunkMaxscl, chunkMinscl + 1.0e-5f);
                chunkMaxcol = math.max(chunkMaxcol, chunkMincol + 1.0e-5f);
                chunkMaxshs = math.max(chunkMaxshs, chunkMinshs + 1.0e-5f);

                // store chunk info
                GaussianSplat2DAsset.ChunkInfo info = default;
                info.posX = new float2(chunkMinpos.x, chunkMaxpos.x);
                info.posY = new float2(chunkMinpos.y, chunkMaxpos.y);
                info.posZ = new float2(chunkMinpos.z, chunkMaxpos.z);
                info.sclX = math.f32tof16(chunkMinscl.x) | (math.f32tof16(chunkMaxscl.x) << 16);
                info.sclY = math.f32tof16(chunkMinscl.y) | (math.f32tof16(chunkMaxscl.y) << 16);
                info.colR = math.f32tof16(chunkMincol.x) | (math.f32tof16(chunkMaxcol.x) << 16);
                info.colG = math.f32tof16(chunkMincol.y) | (math.f32tof16(chunkMaxcol.y) << 16);
                info.colB = math.f32tof16(chunkMincol.z) | (math.f32tof16(chunkMaxcol.z) << 16);
                info.colA = math.f32tof16(chunkMincol.w) | (math.f32tof16(chunkMaxcol.w) << 16);
                info.shR = math.f32tof16(chunkMinshs.x) | (math.f32tof16(chunkMaxshs.x) << 16);
                info.shG = math.f32tof16(chunkMinshs.y) | (math.f32tof16(chunkMaxshs.y) << 16);
                info.shB = math.f32tof16(chunkMinshs.z) | (math.f32tof16(chunkMaxshs.z) << 16);
                chunks[chunkIdx] = info;

                // adjust data to be 0..1 within chunk bounds
                for (int i = splatBegin; i < splatEnd; ++i)
                {
                    InputSplatData s = splatData[i];
                    s.pos = ((float3)s.pos - chunkMinpos) / (chunkMaxpos - chunkMinpos);
                    if (encodeScaleRelativeToChunk != 0)
                        s.scale = (s.scale - chunkMinscl) / (chunkMaxscl - chunkMinscl);
                    s.dc0 = ((float3)s.dc0 - chunkMincol.xyz) / (chunkMaxcol.xyz - chunkMincol.xyz);
                    s.opacity = (s.opacity - chunkMincol.w) / (chunkMaxcol.w - chunkMincol.w);
                    s.sh1 = ((float3) s.sh1 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh2 = ((float3) s.sh2 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh3 = ((float3) s.sh3 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh4 = ((float3) s.sh4 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh5 = ((float3) s.sh5 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh6 = ((float3) s.sh6 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh7 = ((float3) s.sh7 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh8 = ((float3) s.sh8 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh9 = ((float3) s.sh9 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.shA = ((float3) s.shA - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.shB = ((float3) s.shB - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.shC = ((float3) s.shC - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.shD = ((float3) s.shD - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.shE = ((float3) s.shE - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.shF = ((float3) s.shF - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    splatData[i] = s;
                }
            }
        }

        internal static void CreateChunkData(NativeArray<InputSplatData> splatData, string filePath, ref Hash128 dataHash,
            GaussianSplat2DAsset.ScaleFormat scaleFormat)
        {
            int chunkCount = (splatData.Length + GaussianSplat2DAsset.kChunkSize - 1) / GaussianSplat2DAsset.kChunkSize;
            using var chunks = new NativeArray<GaussianSplat2DAsset.ChunkInfo>((splatData.Length + GaussianSplat2DAsset.kChunkSize - 1) / GaussianSplat2DAsset.kChunkSize, Allocator.TempJob);
            CalcChunkDataJob job = new CalcChunkDataJob
            {
                splatData = splatData,
                chunks = chunks,
                encodeScaleRelativeToChunk = scaleFormat != GaussianSplat2DAsset.ScaleFormat.Float32x2 ? 1 : 0,
            };

            job.Schedule(chunkCount, 8).Complete();

            dataHash.Append(ref job.chunks);

            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            fs.Write(job.chunks.Reinterpret<byte>(UnsafeUtility.SizeOf<GaussianSplat2DAsset.ChunkInfo>()));

        }

        static unsafe void EmitEncodedVector(float3 value, byte* output, GaussianSplat2DAsset.VectorFormat format) =>
            GaussianImportPacking.EmitEncodedVector(value, output, (GaussianImportPacking.VectorFormat)(int)format);

        internal static unsafe void EmitEncodedScale(float2 v, byte* outputPtr, GaussianSplat2DAsset.ScaleFormat format)
        {
            if (format != GaussianSplat2DAsset.ScaleFormat.Float32x2)
                v = math.saturate(v);
            switch (format)
            {
                case GaussianSplat2DAsset.ScaleFormat.Float32x2:
                    *(float*)outputPtr = v.x;
                    *(float*)(outputPtr + 4) = v.y;
                    break;
                case GaussianSplat2DAsset.ScaleFormat.Norm16x2:
                    *(uint*)outputPtr = (uint)(v.x * 65535.5f) | ((uint)(v.y * 65535.5f) << 16);
                    break;
                case GaussianSplat2DAsset.ScaleFormat.Norm8x2:
                    *(ushort*)outputPtr = (ushort)((uint)(v.x * 255.5f) | ((uint)(v.y * 255.5f) << 8));
                    break;
            }
        }

        [BurstCompile]
        struct CreatePositionsDataJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<InputSplatData> m_Input;
            public GaussianSplat2DAsset.VectorFormat m_Format;
            public int m_FormatSize;
            [NativeDisableParallelForRestriction] public NativeArray<byte> m_Output;

            public unsafe void Execute(int index)
            {
                byte* outputPtr = (byte*) m_Output.GetUnsafePtr() + index * m_FormatSize;
                EmitEncodedVector(m_Input[index].pos, outputPtr, m_Format);
            }
        }

        [BurstCompile]
        struct CreateOtherDataJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<InputSplatData> m_Input;
            [NativeDisableContainerSafetyRestriction] [ReadOnly] public NativeArray<int> m_SplatSHIndices;
            public GaussianSplat2DAsset.ScaleFormat m_ScaleFormat;
            public int m_FormatSize;
            [NativeDisableParallelForRestriction] public NativeArray<byte> m_Output;

            public unsafe void Execute(int index)
            {
                byte* outputPtr = (byte*) m_Output.GetUnsafePtr() + index * m_FormatSize;

                // rotation: 4 bytes
                {
                    Quaternion rotQ = m_Input[index].rot;
                    float4 rot = new float4(rotQ.x, rotQ.y, rotQ.z, rotQ.w);
                    uint enc = EncodeQuatToNorm10(rot);
                    *(uint*) outputPtr = enc;
                    outputPtr += 4;
                }

                // two tangent scales: 8, 4 or 2 bytes
                EmitEncodedScale(m_Input[index].scale, outputPtr, m_ScaleFormat);
                outputPtr += GaussianSplat2DAsset.GetScaleSize(m_ScaleFormat);

                // SH index
                if (m_SplatSHIndices.IsCreated)
                    *(ushort*) outputPtr = (ushort)m_SplatSHIndices[index];
            }
        }

        void CreatePositionsData(NativeArray<InputSplatData> inputSplats, string filePath, ref Hash128 dataHash)
        {
            int dataLen = inputSplats.Length * GaussianSplat2DAsset.GetVectorSize(m_FormatPos);
            dataLen = NextMultipleOf(dataLen, 8); // serialized as ulong
            using NativeArray<byte> data = new(dataLen, Allocator.TempJob);

            CreatePositionsDataJob job = new CreatePositionsDataJob
            {
                m_Input = inputSplats,
                m_Format = m_FormatPos,
                m_FormatSize = GaussianSplat2DAsset.GetVectorSize(m_FormatPos),
                m_Output = data
            };
            job.Schedule(inputSplats.Length, 8192).Complete();

            dataHash.Append(data);

            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            fs.Write(data);

        }

        void CreateOtherData(NativeArray<InputSplatData> inputSplats, string filePath, ref Hash128 dataHash, NativeArray<int> splatSHIndices)
        {
            int formatSize = GaussianSplat2DAsset.GetOtherSizeNoSHIndex(m_FormatScale);
            if (splatSHIndices.IsCreated)
                formatSize += 2;
            int dataLen = inputSplats.Length * formatSize;

            dataLen = NextMultipleOf(dataLen, 8); // serialized as ulong
            using NativeArray<byte> data = new(dataLen, Allocator.TempJob);

            CreateOtherDataJob job = new CreateOtherDataJob
            {
                m_Input = inputSplats,
                m_SplatSHIndices = splatSHIndices,
                m_ScaleFormat = m_FormatScale,
                m_FormatSize = formatSize,
                m_Output = data
            };
            job.Schedule(inputSplats.Length, 8192).Complete();

            dataHash.Append(data);

            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            fs.Write(data);

        }

        void CreateColorData(NativeArray<InputSplatData> records, string path, ref Hash128 hash)
        {
            var (width, height) = GaussianSplat2DAsset.CalcTextureSize(records.Length);
            GaussianImportColor.Write(records, 12, 204, width, height, (GaussianImportPacking.ColorFormat)(int)m_FormatColor,
                GaussianSplat2DAsset.ColorFormatToGraphics(m_FormatColor), path, ref hash);
        }

        void CreateSHData(NativeArray<InputSplatData> records, string path, ref Hash128 hash, NativeArray<byte> clustered)
        {
            if (clustered.IsCreated) EmitSimpleDataFile(clustered, path, ref hash);
            else GaussianImportSH.Write(records, 24, (GaussianImportPacking.SHFormat)(int)m_FormatSH, path, ref hash);
        }

        public static GaussianSplat2DAsset.CameraInfo[] LoadJsonCamerasFile(string path, bool enabled) =>
            GaussianImportIO.LoadCameras(path, enabled)?.Select(camera => new GaussianSplat2DAsset.CameraInfo
            { pos = camera.pos, axisX = camera.axisX, axisY = camera.axisY, axisZ = camera.axisZ, fov = camera.fov }).ToArray();

    }
}
