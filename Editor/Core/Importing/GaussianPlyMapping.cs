// SPDX-License-Identifier: MIT
using System;
using Gaussians.Core.Editor.Utils;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Gaussians.Core.Editor.Importing
{
    public static class GaussianPlyMapping
    {
        public static unsafe NativeArray<T> Decode<T>(NativeArray<byte> source, PLYVertexLayout layout, string[] fields) where T : unmanaged
        {
            int stride = UnsafeUtility.SizeOf<T>();
            if (fields.Length * 4 != stride) throw new ArgumentException("Property mapping must cover the decoded float record");
            var offsets = new int[fields.Length];
            for (int i = 0; i < fields.Length; ++i) offsets[i] = layout.GetOffset(fields[i], PLYFileReader.ElementType.Float);
            var result = new NativeArray<T>(layout.VertexCount, Allocator.Persistent);
            try
            {
                fixed (int* mapping = offsets)
                    Copy(layout.VertexCount, (byte*)source.GetUnsafeReadOnlyPtr(), layout.VertexStride, (byte*)result.GetUnsafePtr(), stride, mapping);
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        [BurstCompile]
        static unsafe void Copy(int count, byte* source, int sourceStride, byte* destination, int destinationStride, int* offsets)
        {
            for (int row = 0; row < count; ++row)
            {
                for (int field = 0; field < destinationStride / 4; ++field)
                    if (offsets[field] >= 0) *(int*)(destination + field * 4) = *(int*)(source + offsets[field]);
                source += sourceStride; destination += destinationStride;
            }
        }
        public static unsafe void ReorderSH<T>(NativeArray<T> records, int shByteOffset) where T : unmanaged =>
            ReorderSH(records.Length, (byte*)records.GetUnsafePtr(), UnsafeUtility.SizeOf<T>(), shByteOffset);
        [BurstCompile]
        static unsafe void ReorderSH(int count, byte* records, int stride, int offset)
        {
            float* temporary = stackalloc float[45];
            for (int row = 0; row < count; ++row)
            {
                float* sh = (float*)(records + row * stride + offset);
                for (int coefficient = 0; coefficient < 15; ++coefficient)
                    for (int channel = 0; channel < 3; ++channel)
                        temporary[coefficient * 3 + channel] = sh[channel * 15 + coefficient];
                for (int i = 0; i < 45; ++i) sh[i] = temporary[i];
            }
        }
    }
}
