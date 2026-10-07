// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.IO;
using Gaussians.ThreeD.Editor.Utils;
using Gaussians.ThreeD;
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

namespace Gaussians.ThreeD.Editor
{
    public readonly struct GaussianSplat3DImportSettings
    {
        public readonly GaussianSplat3DAsset.VectorFormat Position, Scale;
        public readonly GaussianSplat3DAsset.ColorFormat Color;
        public readonly GaussianSplat3DAsset.SHFormat SH;
        public GaussianSplat3DImportSettings(GaussianSplat3DAsset.VectorFormat position, GaussianSplat3DAsset.VectorFormat scale, GaussianSplat3DAsset.ColorFormat color, GaussianSplat3DAsset.SHFormat sh)
        { Position = position; Scale = scale; Color = color; SH = sh; }
        public static GaussianSplat3DImportSettings Lossless => new(GaussianSplat3DAsset.VectorFormat.Float32, GaussianSplat3DAsset.VectorFormat.Float32, GaussianSplat3DAsset.ColorFormat.Float32x4, GaussianSplat3DAsset.SHFormat.Float32);
        public static GaussianSplat3DImportSettings Medium => new(GaussianSplat3DAsset.VectorFormat.Norm11, GaussianSplat3DAsset.VectorFormat.Norm11, GaussianSplat3DAsset.ColorFormat.Norm8x4, GaussianSplat3DAsset.SHFormat.Norm6);
    }
    [BurstCompile]
    public sealed class GaussianSplat3DAssetBuilder
    {
        const string kProgressTitle = "Creating 3D Gaussian Splat Asset";
        readonly GaussianSplat3DAsset.VectorFormat m_FormatPos, m_FormatScale;
        readonly GaussianSplat3DAsset.ColorFormat m_FormatColor;
        readonly GaussianSplat3DAsset.SHFormat m_FormatSH;
        bool isUsingChunks => m_FormatPos != GaussianSplat3DAsset.VectorFormat.Float32 || m_FormatScale != GaussianSplat3DAsset.VectorFormat.Float32 || m_FormatColor != GaussianSplat3DAsset.ColorFormat.Float32x4 || m_FormatSH != GaussianSplat3DAsset.SHFormat.Float32;
        public GaussianSplat3DAssetBuilder(GaussianSplat3DImportSettings settings)
        { m_FormatPos = settings.Position; m_FormatScale = settings.Scale; m_FormatColor = settings.Color; m_FormatSH = settings.SH; }
        // The builder mutates its input while ordering and normalizing chunks; the caller owns it.
        public unsafe GaussianSplat3DAsset Build(NativeArray<InputSplatData> inputSplats, string outputFolder, string baseName, GaussianSplat3DAsset.CameraInfo[] cameras)
        {
            if (inputSplats.Length == 0 || inputSplats.Length > GaussianSplat3DAsset.kMaxSplats) throw new IOException("Unsupported Gaussian asset count: " + inputSplats.Length);
            ValidateOutputFolder(outputFolder);
            Directory.CreateDirectory(outputFolder);
            NativeArray<int> splatSHIndices = default;
            NativeArray<byte> clusteredSHs = default;
            GaussianSplat3DAsset asset = null;
            try
            {
                float3 boundsMin, boundsMax;
                var boundsJob = new CalcBoundsJob
                {
                    m_BoundsMin = &boundsMin,
                    m_BoundsMax = &boundsMax,
                    m_SplatData = inputSplats
                };
                boundsJob.Schedule().Complete();

                EditorUtility.DisplayProgressBar(kProgressTitle, "Morton reordering", 0.05f);
                ReorderMorton(inputSplats, boundsMin, boundsMax);

                // cluster SHs
                if (m_FormatSH >= GaussianSplat3DAsset.SHFormat.Cluster64k)
                {
                    EditorUtility.DisplayProgressBar(kProgressTitle, "Cluster SHs", 0.2f);
                    ClusterSHs(inputSplats, m_FormatSH, out clusteredSHs, out splatSHIndices);
                }

                EditorUtility.DisplayProgressBar(kProgressTitle, "Creating data objects", 0.7f);
                asset = ScriptableObject.CreateInstance<GaussianSplat3DAsset>();
                asset.Initialize(inputSplats.Length, m_FormatPos, m_FormatScale, m_FormatColor, m_FormatSH, boundsMin, boundsMax, cameras);
                asset.name = baseName;
                // Compute before chunk normalization mutates the source scales/positions.
                CalculateRenderBounds(inputSplats, isUsingChunks, out var renderMin, out var renderMax);
                if (math.all(math.isfinite(renderMin)) && math.all(math.isfinite(renderMax)))
                    asset.SetRenderBounds(renderMin, renderMax);

                var dataHash = new Hash128((uint)asset.splatCount, (uint)asset.formatVersion, 0, 0);
                string pathChunk = $"{outputFolder}/{baseName}_chk.bytes";
                string pathPos = $"{outputFolder}/{baseName}_pos.bytes";
                string pathOther = $"{outputFolder}/{baseName}_oth.bytes";
                string pathCol = $"{outputFolder}/{baseName}_col.bytes";
                string pathSh = $"{outputFolder}/{baseName}_shs.bytes";

                // if we are using full lossless (FP32) data, then do not use any chunking, and keep data as-is
                bool useChunks = isUsingChunks;
                if (useChunks)
                    CreateChunkData(inputSplats, pathChunk, ref dataHash);
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
            [NativeDisableUnsafePtrRestriction] public unsafe float3* m_BoundsMin;
            [NativeDisableUnsafePtrRestriction] public unsafe float3* m_BoundsMax;
            [ReadOnly] public NativeArray<InputSplatData> m_SplatData;

            public unsafe void Execute()
            {
                float3 boundsMin = float.PositiveInfinity;
                float3 boundsMax = float.NegativeInfinity;

                for (int i = 0; i < m_SplatData.Length; ++i)
                {
                    float3 pos = m_SplatData[i].pos;
                    boundsMin = math.min(boundsMin, pos);
                    boundsMax = math.max(boundsMax, pos);
                }
                *m_BoundsMin = boundsMin;
                *m_BoundsMax = boundsMax;
            }
        }

        static void CalculateRenderBounds(NativeArray<InputSplatData> splats, bool chunks,
            out float3 min, out float3 max)
        {
            min = float.PositiveInfinity;
            max = float.NegativeInfinity;
            for (int start = 0; start < splats.Length; start += GaussianSplat3DAsset.kChunkSize)
            {
                int end = math.min(start + GaussianSplat3DAsset.kChunkSize, splats.Length);
                float3 scaleMax = 0;
                for (int i = start; i < end; ++i) scaleMax = math.max(scaleMax, (float3)splats[i].scale);
                // Chunk scales decode from half endpoints in eighth-root space. Include
                // the nonzero-range floor and upward half rounding, then undo that power.
                if (chunks)
                {
                    float3 root = math.pow(scaleMax, 1.0f / 8.0f) + 1.0e-5f;
                    scaleMax = math.pow(new float3(
                        math.f16tof32(math.f32tof16(root.x) + 1),
                        math.f16tof32(math.f32tof16(root.y) + 1),
                        math.f16tof32(math.f32tof16(root.z) + 1)), 8.0f);
                }
                for (int i = start; i < end; ++i)
                {
                    var splat = splats[i];
                    // A sphere enclosing every rotated ellipsoid is conservative even after
                    // quaternion/scale quantization. Nine scale units cover the quad's sqrt(2)
                    // corner expansion at maximum runtime scale 2 (3*sqrt(2)*2 < 9).
                    float radius = math.cmax(chunks ? scaleMax : (float3)splat.scale) * 9.0f;
                    min = math.min(min, (float3)splat.pos - radius);
                    max = math.max(max, (float3)splat.pos + radius);
                }
            }
        }

        static void ReorderMorton(NativeArray<InputSplatData> records, float3 min, float3 max) => GaussianImportOrder.ReorderMorton(records, min, max, 0);

        static bool ClusterSHProgress(float val)
        {
            EditorUtility.DisplayProgressBar(kProgressTitle, $"Cluster SHs ({val:P0})", 0.2f + val * 0.5f);
            return true;
        }

        static void ClusterSHs(NativeArray<InputSplatData> records, GaussianSplat3DAsset.SHFormat format, out NativeArray<byte> table, out NativeArray<int> indices)
        {
            float passes = format switch
            {
                GaussianSplat3DAsset.SHFormat.Cluster64k => .3f, GaussianSplat3DAsset.SHFormat.Cluster32k => .4f,
                GaussianSplat3DAsset.SHFormat.Cluster16k => .5f, GaussianSplat3DAsset.SHFormat.Cluster8k => .8f,
                GaussianSplat3DAsset.SHFormat.Cluster4k => 1.2f, _ => throw new ArgumentOutOfRangeException(nameof(format))
            };
            GaussianImportSH.Cluster(records, 36, GaussianSplat3DAsset.GetSHCount(format, records.Length), passes, ClusterSHProgress, out table, out indices);
        }

        [BurstCompile]
        struct CalcChunkDataJob : IJobParallelFor
        {
            [NativeDisableParallelForRestriction] public NativeArray<InputSplatData> splatData;
            public NativeArray<GaussianSplat3DAsset.ChunkInfo> chunks;

            public void Execute(int chunkIdx)
            {
                float3 chunkMinpos = float.PositiveInfinity;
                float3 chunkMinscl = float.PositiveInfinity;
                float4 chunkMincol = float.PositiveInfinity;
                float3 chunkMinshs = float.PositiveInfinity;
                float3 chunkMaxpos = float.NegativeInfinity;
                float3 chunkMaxscl = float.NegativeInfinity;
                float4 chunkMaxcol = float.NegativeInfinity;
                float3 chunkMaxshs = float.NegativeInfinity;

                int splatBegin = math.min(chunkIdx * GaussianSplat3DAsset.kChunkSize, splatData.Length);
                int splatEnd = math.min((chunkIdx + 1) * GaussianSplat3DAsset.kChunkSize, splatData.Length);

                // calculate data bounds inside the chunk
                for (int i = splatBegin; i < splatEnd; ++i)
                {
                    InputSplatData s = splatData[i];

                    // transform scale to be more uniformly distributed
                    s.scale = math.pow(s.scale, 1.0f / 8.0f);
                    // transform opacity to be more uniformly distributed
                    s.opacity = GaussianUtils.SquareCentered01(s.opacity);
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
                GaussianSplat3DAsset.ChunkInfo info = default;
                info.posX = new float2(chunkMinpos.x, chunkMaxpos.x);
                info.posY = new float2(chunkMinpos.y, chunkMaxpos.y);
                info.posZ = new float2(chunkMinpos.z, chunkMaxpos.z);
                info.sclX = math.f32tof16(chunkMinscl.x) | (math.f32tof16(chunkMaxscl.x) << 16);
                info.sclY = math.f32tof16(chunkMinscl.y) | (math.f32tof16(chunkMaxscl.y) << 16);
                info.sclZ = math.f32tof16(chunkMinscl.z) | (math.f32tof16(chunkMaxscl.z) << 16);
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
                    s.scale = ((float3)s.scale - chunkMinscl) / (chunkMaxscl - chunkMinscl);
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

        static void CreateChunkData(NativeArray<InputSplatData> splatData, string filePath, ref Hash128 dataHash)
        {
            int chunkCount = (splatData.Length + GaussianSplat3DAsset.kChunkSize - 1) / GaussianSplat3DAsset.kChunkSize;
            using var chunks = new NativeArray<GaussianSplat3DAsset.ChunkInfo>((splatData.Length + GaussianSplat3DAsset.kChunkSize - 1) / GaussianSplat3DAsset.kChunkSize, Allocator.TempJob);
            CalcChunkDataJob job = new CalcChunkDataJob
            {
                splatData = splatData,
                chunks = chunks,
            };

            job.Schedule(chunkCount, 8).Complete();

            dataHash.Append(ref job.chunks);

            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            fs.Write(job.chunks.Reinterpret<byte>(UnsafeUtility.SizeOf<GaussianSplat3DAsset.ChunkInfo>()));

        }

        static unsafe void EmitEncodedVector(float3 value, byte* output, GaussianSplat3DAsset.VectorFormat format) =>
            GaussianImportPacking.EmitEncodedVector(value, output, (GaussianImportPacking.VectorFormat)(int)format);

        [BurstCompile]
        struct CreatePositionsDataJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<InputSplatData> m_Input;
            public GaussianSplat3DAsset.VectorFormat m_Format;
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
            public GaussianSplat3DAsset.VectorFormat m_ScaleFormat;
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

                // scale: 6, 4 or 2 bytes
                EmitEncodedVector(m_Input[index].scale, outputPtr, m_ScaleFormat);
                outputPtr += GaussianSplat3DAsset.GetVectorSize(m_ScaleFormat);

                // SH index
                if (m_SplatSHIndices.IsCreated)
                    *(ushort*) outputPtr = (ushort)m_SplatSHIndices[index];
            }
        }

        void CreatePositionsData(NativeArray<InputSplatData> inputSplats, string filePath, ref Hash128 dataHash)
        {
            int dataLen = inputSplats.Length * GaussianSplat3DAsset.GetVectorSize(m_FormatPos);
            dataLen = NextMultipleOf(dataLen, 8); // serialized as ulong
            using NativeArray<byte> data = new(dataLen, Allocator.TempJob);

            CreatePositionsDataJob job = new CreatePositionsDataJob
            {
                m_Input = inputSplats,
                m_Format = m_FormatPos,
                m_FormatSize = GaussianSplat3DAsset.GetVectorSize(m_FormatPos),
                m_Output = data
            };
            job.Schedule(inputSplats.Length, 8192).Complete();

            dataHash.Append(data);

            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            fs.Write(data);

        }

        void CreateOtherData(NativeArray<InputSplatData> inputSplats, string filePath, ref Hash128 dataHash, NativeArray<int> splatSHIndices)
        {
            int formatSize = GaussianSplat3DAsset.GetOtherSizeNoSHIndex(m_FormatScale);
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
            var (width, height) = GaussianSplat3DAsset.CalcTextureSize(records.Length);
            GaussianImportColor.Write(records, 24, 216, width, height, (GaussianImportPacking.ColorFormat)(int)m_FormatColor,
                GaussianSplat3DAsset.ColorFormatToGraphics(m_FormatColor), path, ref hash);
        }

        void CreateSHData(NativeArray<InputSplatData> records, string path, ref Hash128 hash, NativeArray<byte> clustered)
        {
            if (clustered.IsCreated) EmitSimpleDataFile(clustered, path, ref hash);
            else GaussianImportSH.Write(records, 36, (GaussianImportPacking.SHFormat)(int)m_FormatSH, path, ref hash);
        }

        public static GaussianSplat3DAsset.CameraInfo[] LoadJsonCamerasFile(string path, bool enabled) =>
            GaussianImportIO.LoadCameras(path, enabled)?.Select(camera => new GaussianSplat3DAsset.CameraInfo
            { pos = camera.pos, axisX = camera.axisX, axisY = camera.axisY, axisZ = camera.axisZ, fov = camera.fov }).ToArray();

    }
}
