// SPDX-License-Identifier: MIT
using System;
using Gaussians.Core.Editor.Utils;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Gaussians.Core.Editor.Importing
{
    public static class GaussianImportSH
    {
        static int Size(GaussianImportPacking.SHFormat format) => format switch
        {
            GaussianImportPacking.SHFormat.Float32 => 192, GaussianImportPacking.SHFormat.Float16 => 96,
            GaussianImportPacking.SHFormat.Norm11 => 60, GaussianImportPacking.SHFormat.Norm6 => 32,
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        public static void Write<T>(NativeArray<T> records, int shOffset, GaussianImportPacking.SHFormat format, string path, ref Hash128 hash) where T : unmanaged
        {
            using var encoded = new NativeArray<byte>(checked(records.Length * Size(format)), Allocator.TempJob);
            Encode(records.Reinterpret<byte>(UnsafeUtility.SizeOf<T>()), UnsafeUtility.SizeOf<T>(), shOffset, records.Length, format, encoded);
            GaussianImportIO.EmitSimpleDataFile(encoded, path, ref hash);
        }
        public static void Cluster<T>(NativeArray<T> records, int shOffset, int tableCount, float passes, Func<float, bool> progress,
            out NativeArray<byte> table, out NativeArray<int> indices) where T : unmanaged
        {
            table = default; indices = default;
            try
            {
                indices = new NativeArray<int>(records.Length, Allocator.Persistent);
                table = new NativeArray<byte>(checked(tableCount * 96), Allocator.Persistent);
                if (tableCount >= records.Length)
                {
                    // The runtime format still expects the fixed-size table. Populate its used
                    // entries and identity indices rather than dropping SH for small clouds.
                    Encode(records.Reinterpret<byte>(UnsafeUtility.SizeOf<T>()), UnsafeUtility.SizeOf<T>(), shOffset,
                        records.Length, GaussianImportPacking.SHFormat.Float16, table);
                    for (int i = 0; i < indices.Length; ++i) indices[i] = i;
                    return;
                }
                using var data = new NativeArray<float>(checked(records.Length * 45), Allocator.TempJob);
                new GatherJob { Records = records.Reinterpret<byte>(UnsafeUtility.SizeOf<T>()), Stride = UnsafeUtility.SizeOf<T>(), Offset = shOffset, Data = data }
                    .Schedule(records.Length, 4096).Complete();
                using var means = new NativeArray<float>(checked(tableCount * 45), Allocator.TempJob);
                KMeansClustering.Calculate(45, data, 2048, passes, progress, means, indices);
                Encode(means.Reinterpret<byte>(4), 45 * 4, 0, tableCount, GaussianImportPacking.SHFormat.Float16, table);
            }
            catch
            {
                if (table.IsCreated) table.Dispose(); if (indices.IsCreated) indices.Dispose();
                table = default; indices = default; throw;
            }
        }
        static void Encode(NativeArray<byte> input, int stride, int offset, int count, GaussianImportPacking.SHFormat format, NativeArray<byte> output) =>
            new EncodeJob { Input = input, Stride = stride, Offset = offset, Format = format, OutputStride = Size(format), Output = output }.Schedule(count, 8192).Complete();
        [BurstCompile]
        struct GatherJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<byte> Records;
            public int Stride, Offset;
            [NativeDisableParallelForRestriction] public NativeArray<float> Data;
            public unsafe void Execute(int index) => UnsafeUtility.MemCpy((float*)Data.GetUnsafePtr() + index * 45,
                (byte*)Records.GetUnsafeReadOnlyPtr() + index * Stride + Offset, 45 * 4);
        }
        [BurstCompile]
        struct EncodeJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<byte> Input;
            public int Stride, Offset, OutputStride;
            public GaussianImportPacking.SHFormat Format;
            [NativeDisableParallelForRestriction] public NativeArray<byte> Output;
            public unsafe void Execute(int index)
            {
                float3* sh = (float3*)((byte*)Input.GetUnsafeReadOnlyPtr() + index * Stride + Offset);
                byte* output = (byte*)Output.GetUnsafePtr() + index * OutputStride;
                for (int coefficient = 0; coefficient < 15; ++coefficient)
                {
                    float3 value = sh[coefficient];
                    switch (Format)
                    {
                        case GaussianImportPacking.SHFormat.Float32: ((float3*)output)[coefficient] = value; break;
                        case GaussianImportPacking.SHFormat.Float16: ((half3*)output)[coefficient] = new half3(value); break;
                        case GaussianImportPacking.SHFormat.Norm11: ((uint*)output)[coefficient] = GaussianImportPacking.EncodeFloat3ToNorm11(value); break;
                        case GaussianImportPacking.SHFormat.Norm6: ((ushort*)output)[coefficient] = GaussianImportPacking.EncodeFloat3ToNorm565(value); break;
                    }
                }
            }
        }
    }
}
