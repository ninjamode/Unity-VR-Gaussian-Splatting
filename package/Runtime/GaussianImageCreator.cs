using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GaussianSplatting.Runtime;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;


namespace GaussianSplatting.Runtime
{
    public class GaussianImageCreator
    {
        static int SplatIndexToTextureIndex(uint idx)                                                                                                                                                                                  
        {
            uint2 xy = GaussianUtils.DecodeMorton2D_16x16(idx);
            uint width = GaussianSplatAsset.kTextureWidth / 16;
            idx >>= 8;
            uint x = (idx % width) * 16 + xy.x;
            uint y = (idx / width) * 16 + xy.y;
            return (int)(y * GaussianSplatAsset.kTextureWidth + x);
        }
        
        [BurstCompile]
        struct ConvertColorJob : IJobParallelFor
        {
            public int width, height;
            [ReadOnly] public NativeArray<float4> inputData;
            [NativeDisableParallelForRestriction] public NativeArray<byte> outputData;
            public GaussianSplatAsset.ColorFormat format;
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
                        case GaussianSplatAsset.ColorFormat.Float32x4:
                        {
                            *(float4*) dstPtr = pix;
                        }
                            break;
                        case GaussianSplatAsset.ColorFormat.Float16x4:
                        {
                            half4 enc = new half4(pix);
                            *(half4*) dstPtr = enc;
                        }
                            break;
                        case GaussianSplatAsset.ColorFormat.Norm8x4:
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
        
        [BurstCompile]
        struct CreateColorDataJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float4> m_Input;
            [NativeDisableParallelForRestriction] public NativeArray<float4> m_Output;

            public void Execute(int index)
            {
                var color = m_Input[index];
                int i = SplatIndexToTextureIndex((uint)index);
                m_Output[i] = color;
            }
        }
        
        public static NativeArray<byte> CreateColorData(NativeArray<float4> color, GaussianSplatAsset.ColorFormat gFormat)
        {
            var (width, height) = GaussianSplatAsset.CalcTextureSize(color.Length);
            NativeArray<float4> data = new(width * height, Allocator.TempJob);

            CreateColorDataJob job = new CreateColorDataJob
            {
                m_Input = color,
                m_Output = data
            };
            job.Schedule(color.Length, 8192).Complete();
            

            GraphicsFormat gfxFormat = GaussianSplatAsset.ColorFormatToGraphics(gFormat);
            int dstSize = (int)GraphicsFormatUtility.ComputeMipmapSize(width, height, gfxFormat);

            NativeArray<byte> imageData = default;

            if (GraphicsFormatUtility.IsCompressedFormat(gfxFormat))
            {
                Texture2D tex = new Texture2D(width, height, GraphicsFormat.R32G32B32A32_SFloat, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.DontUploadUponCreate);
                tex.SetPixelData(data, 0);
                
#if UNITY_EDITOR               
                EditorUtility.CompressTexture(tex, GraphicsFormatUtility.GetTextureFormat(gfxFormat), 100);
#else
                // TODO: Do we need to fix this?
                Debug.LogError("Texture compression not available at runtime");
#endif
                
                imageData = tex.GetPixelData<byte>(0);
            }
            else
            {
                ConvertColorJob jobConvert = new ConvertColorJob
                {
                    width = width,
                    height = height,
                    inputData = data,
                    format = gFormat,
                    outputData = new NativeArray<byte>(dstSize, Allocator.TempJob),
                    formatBytesPerPixel = dstSize / width / height
                };
                jobConvert.Schedule(height, 1).Complete();
                imageData = jobConvert.outputData;
            }
            
            data.Dispose();
            return imageData;
        }
    }
}