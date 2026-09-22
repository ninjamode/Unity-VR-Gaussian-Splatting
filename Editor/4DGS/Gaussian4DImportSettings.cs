using Gaussians.ThreeD;
using UnityEngine;

namespace Gaussians.FourD.Editor
{
    // Editor-only ownership record. Only these generated files may be replaced/removed on reimport.
    public sealed class Gaussian4DImportSettings : ScriptableObject
    {
        public string sourceDirectory;
        public Gaussian4DBackends backends;
        public GaussianSplatSHStorage canonicalSH;
        public bool mortonOrder = true;
        public string[] generatedFiles;
        public long runtimeTensorBytes, canonicalBytes, onnxBytes;
        public int removedOnnxSqueezes;
    }
}
