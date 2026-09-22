using System;
using System.Collections.Generic;
using Unity.InferenceEngine;
using UnityEngine;

namespace Gaussians.FourD.Inference
{
    /// <summary>Owns a GPU worker. Inputs remain caller-owned until execution completes.
    /// Outputs/buffers remain worker-owned and are invalidated by the next schedule/dispose.</summary>
    public sealed class GaussianSplat4DInference : IDisposable
    {
        public static readonly string[] Attributes = { "positions", "log_scales", "rotations_wxyz", "opacity_logits", "sh" };
        public Model Model { get; }
        public Worker Worker { get; private set; }

        public GaussianSplat4DInference(GaussianSplat4DInferenceAsset asset)
        {
            if (!SystemInfo.supportsComputeShaders) throw new NotSupportedException("GPU compute shaders are required.");
            if (!asset || !asset.Model || !asset.Data) throw new ArgumentException("A complete 4D inference asset is required.");
            Model = ModelLoader.Load(asset.Model);
            Worker = new Worker(Model, BackendType.GPUCompute);
        }

        public void Schedule(IReadOnlyDictionary<string, Tensor<float>> inputs)
        {
            if (Worker == null) throw new ObjectDisposedException(nameof(GaussianSplat4DInference));
            foreach (var input in Model.inputs)
            {
                if (!inputs.TryGetValue(input.name, out var tensor)) throw new ArgumentException("Missing input: " + input.name);
                // Disabled heads can be identity outputs; keep their source inputs on GPU too.
                ComputeTensorData.Pin(tensor);
                Worker.SetInput(input.name, tensor);
            }
#if UNITY_EDITOR
            Gaussian4DInferenceEditorLifetime.EnsureCleanupRegistered();
#endif
            Worker.Schedule();
        }

        public Tensor<float> GetOutput(string attribute)
        {
            if (Worker == null) throw new ObjectDisposedException(nameof(GaussianSplat4DInference));
            return Worker.PeekOutput("deformed_" + attribute) as Tensor<float>
                ?? throw new InvalidOperationException("Missing FP32 output: " + attribute);
        }

        public ComputeBuffer GetOutputBuffer(string attribute)
        {
            var output = GetOutput(attribute);
            // Do not silently upload a CPU result and call it GPU inference.
            if (output.dataOnBackend is not ComputeTensorData data)
                throw new InvalidOperationException("Output is not GPU-resident: " + attribute);
            return data.buffer;
        }

        public void Dispose()
        {
            Worker?.Dispose();
            Worker = null;
        }
    }
}
