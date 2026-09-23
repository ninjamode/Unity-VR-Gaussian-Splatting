using UnityEngine;

namespace Gaussians.Benchmark
{
    [CreateAssetMenu(menuName = "Gaussians/Benchmark Renderer Settings")]
    public sealed class GaussianBenchmarkRendererSettings : ScriptableObject
    {
        public GaussianBenchmarkOverrides settings = new();
    }
}
