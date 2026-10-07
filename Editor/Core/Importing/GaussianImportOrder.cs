// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Gaussians.Core.Editor.Importing
{
    public static class GaussianImportOrder
    {
        public static void ReorderMorton<T>(NativeArray<T> records, float3 min, float3 max, int positionOffset) where T : unmanaged
        {
            using var order = new NativeArray<(ulong, int)>(records.Length, Allocator.TempJob);
            var job = new MortonJob { Records = records.Reinterpret<byte>(UnsafeUtility.SizeOf<T>()), Stride = UnsafeUtility.SizeOf<T>(),
                PositionOffset = positionOffset, Min = min, InvSize = math.select(math.rcp(max - min), 0, max == min), Order = order };
            job.Schedule(records.Length, 4096).Complete();
            order.Sort(new OrderComparer());
            using var copy = new NativeArray<T>(records, Allocator.TempJob);
            for (int i = 0; i < records.Length; ++i) records[i] = copy[order[i].Item2];
        }
        [BurstCompile]
        struct MortonJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<byte> Records;
            public int Stride, PositionOffset;
            public float3 Min, InvSize;
            public NativeArray<(ulong, int)> Order;
            public unsafe void Execute(int index)
            {
                float3 position = *(float3*)((byte*)Records.GetUnsafeReadOnlyPtr() + index * Stride + PositionOffset);
                uint3 quantized = (uint3)((position - Min) * InvSize * ((1 << 21) - 1));
                Order[index] = (GaussianImportPacking.MortonEncode3(quantized), index);
            }
        }
        struct OrderComparer : IComparer<(ulong, int)>
        {
            public int Compare((ulong, int) a, (ulong, int) b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2.CompareTo(b.Item2);
        }
    }
}
