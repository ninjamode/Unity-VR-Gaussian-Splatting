// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Gaussians.Core
{
    // One renderer owns this payload and buffer; cutout components remain externally owned.
    internal sealed class GaussianCutoutBuffer : IDisposable
    {
        GaussianCutout.ShaderData[] m_Data = new GaussianCutout.ShaderData[1];
        bool m_HasPayload;
        bool m_Dirty = true;

        internal int Count { get; private set; }
        internal int Revision { get; private set; }
        internal GraphicsBuffer Buffer { get; private set; }

        internal void Refresh(IReadOnlyList<GaussianCutout> cutouts, Matrix4x4 objectToWorld)
        {
            int count = cutouts?.Count ?? 0;
            bool changed = !m_HasPayload || Count != count;
            if (m_Data.Length < Math.Max(1, count))
                Array.Resize(ref m_Data, Math.Max(count, m_Data.Length * 2));
            for (int i = 0; i < count; ++i)
            {
                var data = GaussianCutout.GetShaderData(cutouts[i], objectToWorld);
                changed |= !m_Data[i].matrix.Equals(data.matrix) || m_Data[i].typeAndFlags != data.typeAndFlags;
                m_Data[i] = data;
            }
            if (count == 0) m_Data[0] = default;
            Count = count;
            m_HasPayload = true;
            if (changed)
            {
                unchecked { ++Revision; }
                m_Dirty = true;
            }
        }

        internal void EnsureUploaded()
        {
            int size = Math.Max(1, Count);
            if (Buffer == null || Buffer.count != size)
            {
                Buffer?.Dispose();
                Buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, size,
                    Marshal.SizeOf<GaussianCutout.ShaderData>()) { name = "GaussianCutouts" };
                m_Dirty = true;
            }
            if (!m_Dirty) return;
            Buffer.SetData(m_Data, 0, 0, size);
            m_Dirty = false;
        }

        public void Dispose()
        {
            Buffer?.Dispose();
            Buffer = null;
            m_Dirty = true;
        }
    }
}
