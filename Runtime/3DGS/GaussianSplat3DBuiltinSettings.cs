// SPDX-License-Identifier: MIT
using UnityEngine;

namespace Gaussians.ThreeD
{
    /// <summary>Optional Built-in camera settings for the complete 3D composite group.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(Camera))]
    [AddComponentMenu("Gaussians/3D/Built-in Camera Settings")]
    public sealed class GaussianSplat3DBuiltinSettings : MonoBehaviour
    {
        [Tooltip("Convert the accumulated 3D group from gamma to linear. Enabled preserves legacy 3D color. Direct renderers control conversion individually. Only used by the Built-in pipeline.")]
        public bool m_ConvertCompositeGammaToLinear = true;
    }
}
