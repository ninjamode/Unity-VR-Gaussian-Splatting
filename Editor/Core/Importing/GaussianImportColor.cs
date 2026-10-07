// SPDX-License-Identifier: MIT
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
namespace Gaussians.Core.Editor.Importing
{
    public static class GaussianImportColor
    {
        public static void Write<T>(NativeArray<T> records, int colorOffset, int opacityOffset, int width, int height,
            GaussianImportPacking.ColorFormat format, GraphicsFormat graphicsFormat, string path, ref Hash128 hash) where T : unmanaged
        {
            using var colors = new NativeArray<float4>(checked(width * height), Allocator.TempJob);
            new GatherJob { Input = records.Reinterpret<byte>(UnsafeUtility.SizeOf<T>()), Stride = UnsafeUtility.SizeOf<T>(),
                ColorOffset = colorOffset, OpacityOffset = opacityOffset, Width = width, Output = colors }.Schedule(records.Length, 8192).Complete();
            hash.Append(colors); hash.Append((int)format);
            if (GraphicsFormatUtility.IsCompressedFormat(graphicsFormat))
            {
                var texture = new Texture2D(width, height, GraphicsFormat.R32G32B32A32_SFloat, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.DontUploadUponCreate);
                try { texture.SetPixelData(colors, 0); EditorUtility.CompressTexture(texture, GraphicsFormatUtility.GetTextureFormat(graphicsFormat), 100);
                    GaussianImportIO.Write(texture.GetPixelData<byte>(0), path); }
                finally { UnityEngine.Object.DestroyImmediate(texture); }
            }
            else
            {
                int size = (int)GraphicsFormatUtility.ComputeMipmapSize(width, height, graphicsFormat);
                using var output = new NativeArray<byte>(size, Allocator.TempJob);
                new ConvertColorJob { width = width, height = height, inputData = colors, format = format,
                    outputData = output, formatBytesPerPixel = size / width / height }.Schedule(height, 1).Complete();
                GaussianImportIO.Write(output, path);
            }
        }
        [BurstCompile]
        struct GatherJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<byte> Input;
            public int Stride, ColorOffset, OpacityOffset, Width;
            [NativeDisableParallelForRestriction] public NativeArray<float4> Output;
            public unsafe void Execute(int index)
            {
                byte* row = (byte*)Input.GetUnsafeReadOnlyPtr() + index * Stride;
                Output[GaussianImportPacking.TextureIndex((uint)index, Width)] = new float4(*(float3*)(row + ColorOffset), *(float*)(row + OpacityOffset));
            }
        }
        [BurstCompile]
        struct ConvertColorJob : IJobParallelFor
        {
            public int width, height;
            [ReadOnly] public NativeArray<float4> inputData;
            [NativeDisableParallelForRestriction] public NativeArray<byte> outputData;
            public GaussianImportPacking.ColorFormat format;
            public int formatBytesPerPixel;

            public unsafe void Execute(int y)
            {
                int srcIdx = y * width;
                byte* dstPtr = (byte*) outputData.GetUnsafePtr() + y * width * formatBytesPerPixel;
                for (int x = 0; x < width; ++x)
                {
                    float4 pix = inputData[srcIdx];

                    switch (format)
                    {
                        case GaussianImportPacking.ColorFormat.Float32x4:
                        {
                            *(float4*) dstPtr = pix;
                        }
                            break;
                        case GaussianImportPacking.ColorFormat.Float16x4:
                        {
                            half4 enc = new half4(pix);
                            *(half4*) dstPtr = enc;
                        }
                            break;
                        case GaussianImportPacking.ColorFormat.Norm8x4:
                        {
                            pix = math.saturate(pix);
                            uint enc = (uint)(pix.x * 255.5f) | ((uint)(pix.y * 255.5f) << 8) | ((uint)(pix.z * 255.5f) << 16) | ((uint)(pix.w * 255.5f) << 24);
                            *(uint*) dstPtr = enc;
                        }
                            break;
                    }

                    srcIdx++;
                    dstPtr += formatBytesPerPixel;
                }
            }
        }

    }
}
