using UnityEngine;

namespace Gaussians.Benchmark
{
    /// <summary>Attach to the experiment root. Runs use trial clones; Show Pose applies hooks to the authored scene.</summary>
    public abstract class GaussianBenchmarkExtension : MonoBehaviour
    {
        /// <summary>Capture runtime state before originals are temporarily disabled.</summary>
        public virtual void CaptureSourceState() { }
        public virtual void RestoreSourceState() { }
        public virtual string Validate(GaussianBenchmarkExperiment experiment) => null;
        /// <summary>Called while the selected subject is inactive. Restrict changes to this experiment hierarchy.</summary>
        public virtual void Configure(GameObject subject, string variantId) { }
        /// <summary>Called once after activation; pause automatic controllers here.</summary>
        public virtual void Begin() { }
        /// <summary>Called before rendering; sample restarts at zero after warm-up.</summary>
        public virtual void SetSample(int sample, float stepSeconds) { }
        public virtual string CheckError() => null;
        public virtual string Describe() => GetType().FullName;
    }
}
