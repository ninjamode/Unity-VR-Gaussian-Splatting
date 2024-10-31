// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    public class GaussianCutout : MonoBehaviour
    {
        public enum Type
        {
            Ellipsoid,
            Box
        }

        public Type m_Type = Type.Ellipsoid;
        public bool m_Invert = false;
        public bool[] layersToCut = Array.Empty<bool>();

        public unsafe struct ShaderData // match GaussianCutoutShaderData in CS
        {
            public Matrix4x4 matrix;
            public uint typeAndFlags;
            public fixed int cutIndices[8]; // Four layers max right now
        }
        
        public static ShaderData GetShaderData(GaussianCutout self, Matrix4x4 rendererMatrix, GaussianSplatAsset asset)
        {
            ShaderData sd = default;
            if (!(self && self.isActiveAndEnabled))
            {
                sd.typeAndFlags = ~0u;
                return sd;
            }

            unsafe // Initialize cut data to invalid value
            {
                for (int i = 0; i < 8; i++)
                {
                    sd.cutIndices[i] = -1;
                }
            }
            
            var tr = self.transform;
            sd.matrix = tr.worldToLocalMatrix * rendererMatrix;
            sd.typeAndFlags = ((uint)self.m_Type) | (self.m_Invert ? 0x100u : 0u);
            
            // Doing this every frame is not very performant, should only run when properties change
            for (int layer = 0; layer < Math.Min(4, self.layersToCut.Length); layer++)
            {
                if (self.layersToCut[layer] && asset.layerInfo.TryGetValue(layer, out int count))
                {
                    int idxFrom = asset.layerInfo.Where(kv => kv.Key < layer).Sum(kv => kv.Value);
                    unsafe
                    {
                        sd.cutIndices[layer * 2] = idxFrom;
                        sd.cutIndices[layer * 2 + 1] = idxFrom + count;
                    }
                }
            }

            return sd;
        }

#if UNITY_EDITOR
        public void OnDrawGizmos()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            var color = Color.magenta;
            color.a = 0.2f;
            if (Selection.Contains(gameObject))
                color.a = 0.9f;
            else
            {
                // mid amount of alpha if a GS object that contains us as a cutout is selected
                var activeGo = Selection.activeGameObject;
                if (activeGo != null)
                {
                    var activeSplat = activeGo.GetComponent<GaussianSplatRenderer>();
                    if (activeSplat != null)
                    {
                        if (activeSplat.m_Cutouts != null && activeSplat.m_Cutouts.Contains(this))
                            color.a = 0.5f;
                    }
                }
            }

            Gizmos.color = color;
            if (m_Type == Type.Ellipsoid)
            {
                Gizmos.DrawWireSphere(Vector3.zero, 1.0f);
            }
            if (m_Type == Type.Box)
            {
                Gizmos.DrawWireCube(Vector3.zero, Vector3.one * 2);
            }
        }
#endif // #if UNITY_EDITOR
    }
}
