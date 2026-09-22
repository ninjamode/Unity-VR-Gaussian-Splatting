using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Gaussians.FourD.Inference;
using Newtonsoft.Json.Linq;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gaussians.FourD.Editor
{
    [Serializable]
    public sealed class Gaussian4DAttributeResult
    {
        public string attribute;
        public bool passed;
        public int elements, failedElements;
        public double maxAbsolute, rmse, activatedMaxAbsolute, quaternionMaxAngleDegrees;
        public string error;
    }

    [Serializable]
    public sealed class Gaussian4DFixtureResult
    {
        public string name;
        public int count;
        public bool passed;
        public double wallMilliseconds;
        public List<Gaussian4DAttributeResult> attributes = new();
    }

    [Serializable]
    public sealed class Gaussian4DValidationReport
    {
        public string asset, unityVersion, inferenceVersion, device, graphicsAPI, error;
        public string backend = "GPUCompute";
        public bool passed;
        public double atol, rtol, angleDegrees;
        public string[] cpuFallbackLayers;
        public List<Gaussian4DFixtureResult> fixtures = new();
    }

    public static class GaussianSplat4DValidation
    {
        const string CopyShaderPath = "Packages/net.kleinbeck.gaussians/Shaders/4DGS/CopyInferenceOutput.compute";

        public static Gaussian4DAttributeResult Compare(string attribute, float[] expected, float[] actual,
            double atol, double rtol, double angleTolerance)
        {
            var result = new Gaussian4DAttributeResult { attribute = attribute, elements = expected.Length, passed = true };
            if (expected.Length == 0 || expected.Length != actual.Length || (attribute == "rotations_wxyz" && expected.Length % 4 != 0))
            {
                result.passed = false;
                result.error = "Invalid output length";
                return result;
            }
            double squares = 0;
            for (int i = 0; i < expected.Length; i++)
            {
                double a = expected[i], b = actual[i];
                if (!Finite(a) || !Finite(b)) { result.failedElements++; result.error = "Nonfinite output"; continue; }
                double error = Math.Abs(a - b);
                result.maxAbsolute = Math.Max(result.maxAbsolute, error);
                squares += error * error;
                if (error > atol + rtol * Math.Abs(a)) result.failedElements++;
                if (attribute == "log_scales" || attribute == "opacity_logits")
                {
                    double aa = attribute == "log_scales" ? Math.Exp(a) : Sigmoid(a);
                    double bb = attribute == "log_scales" ? Math.Exp(b) : Sigmoid(b);
                    if (!Finite(aa) || !Finite(bb)) { result.passed = false; result.error = "Nonfinite activated output"; }
                    else
                    {
                        result.activatedMaxAbsolute = Math.Max(result.activatedMaxAbsolute, Math.Abs(aa - bb));
                        if (Math.Abs(aa - bb) > atol + rtol * Math.Abs(aa)) result.passed = false;
                    }
                }
            }
            if (attribute == "rotations_wxyz")
            {
                for (int i = 0; i < expected.Length; i += 4)
                {
                    double a2 = 0, b2 = 0, dot = 0;
                    for (int j = 0; j < 4; j++)
                    {
                        a2 += (double)expected[i+j] * expected[i+j];
                        b2 += (double)actual[i+j] * actual[i+j];
                        dot += (double)expected[i+j] * actual[i+j];
                    }
                    if (!Finite(a2+b2+dot) || a2 <= 1e-24 || b2 <= 1e-24)
                    { result.passed = false; result.error = "Invalid quaternion"; continue; }
                    double angle = 2 * Math.Acos(Math.Min(1, Math.Abs(dot / Math.Sqrt(a2*b2)))) * 180 / Math.PI;
                    result.quaternionMaxAngleDegrees = Math.Max(result.quaternionMaxAngleDegrees, angle);
                    if (angle > angleTolerance) result.passed = false;
                }
            }
            result.rmse = Math.Sqrt(squares / Math.Max(1, expected.Length));
            result.passed &= result.failedElements == 0;
            return result;
        }

        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        static double Sigmoid(double value) => value >= 0 ? 1 / (1 + Math.Exp(-value)) : Math.Exp(value) / (1 + Math.Exp(value));

        static async Task<float[]> CopyAndReadback(ComputeShader shader, ComputeBuffer input, int count)
        {
            using var output = new ComputeBuffer(count, sizeof(float));
            int kernel = shader.FindKernel("CopyOutput");
            shader.SetBuffer(kernel, "_InferenceOutput", input);
            shader.SetBuffer(kernel, "_ValidationCopy", output);
            shader.SetInt("_Count", count);
            shader.Dispatch(kernel, (count + 63) / 64, 1, 1);
            var completion = new TaskCompletionSource<float[]>();
            var pending = AsyncGPUReadback.Request(output, request =>
            {
                try
                {
                    if (request.hasError) throw new InvalidOperationException("GPU buffer readback failed.");
                    completion.SetResult(request.GetData<float>().ToArray());
                }
                catch (Exception error) { completion.SetException(error); }
            });
            pending.forcePlayerLoopUpdate = true;
            return await completion.Task;
        }

        static string[] FallbackLayers(GaussianSplat4DInference inference)
        {
            // Diagnostic only, isolated here because 2.6 does not expose the backend plan publicly.
            var field = typeof(Worker).GetField("m_LayerCPUFallback", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(inference.Worker) is not HashSet<int> ids)
                return new[] { "Unavailable in this inference package version" };
            return inference.Model.layers.Where(layer => ids.Contains(layer.outputs[0])).Select(layer => layer.ToString()).ToArray();
        }

        public static async Task<Gaussian4DValidationReport> RunAsync(GaussianSplat4DInferenceAsset asset,
            Action<string> progress = null, CancellationToken cancellation = default)
        {
            var report = new Gaussian4DValidationReport { asset = asset ? AssetDatabase.GetAssetPath(asset) : "",
                unityVersion = Application.unityVersion, device = SystemInfo.graphicsDeviceName,
                graphicsAPI = SystemInfo.graphicsDeviceType.ToString(),
                inferenceVersion = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(Worker).Assembly)?.version };
            EditorApplication.update += EditorApplication.QueuePlayerLoopUpdate;
            try
            {
                if (!asset || !asset.Data) throw new ArgumentException("Select a 4D inference asset.");
                if (!SystemInfo.supportsAsyncGPUReadback) throw new NotSupportedException("Asynchronous GPU readback is required for validation.");
                var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(CopyShaderPath);
                if (!shader) throw new InvalidOperationException("Validation copy shader is missing.");
                var manifest = JObject.Parse(asset.Data.Manifest.text);
                var settings = manifest["validation_settings"];
                report.atol = (double)settings["atol"];
                report.rtol = (double)settings["rtol"];
                report.angleDegrees = (double)settings["angle_degrees"];
                if (!Finite(report.atol) || report.atol <= 0 || !Finite(report.rtol) || report.rtol < 0 || !Finite(report.angleDegrees) || report.angleDegrees <= 0)
                    throw new InvalidOperationException("Invalid numerical tolerances in manifest.");
                using var inference = new GaussianSplat4DInference(asset);
                report.cpuFallbackLayers = FallbackLayers(inference);
                foreach (var fixture in manifest["fixtures"])
                {
                    cancellation.ThrowIfCancellationRequested();
                    string name = (string)fixture["name"];
                    progress?.Invoke(name);
                    var watch = Stopwatch.StartNew();
                    var inputs = new Dictionary<string, Tensor<float>>();
                    try
                    {
                        foreach (var input in ((JObject)fixture["inputs"]).Properties())
                        {
                            var data = asset.Data.GetTensor((string)input.Value);
                            inputs.Add(input.Name, new Tensor<float>(new TensorShape(data.shape), data.ReadFloats()));
                        }
                        var result = new Gaussian4DFixtureResult { name = name, count = inputs["positions"].shape[0], passed = true };
                        inference.Schedule(inputs);
                        foreach (string attribute in GaussianSplat4DInference.Attributes)
                        {
                            var expected = asset.Data.GetTensor((string)fixture["outputs"][attribute]);
                            var tensor = inference.GetOutput(attribute);
                            if (!expected.shape.SequenceEqual(tensor.shape.ToArray())) throw new InvalidOperationException(name + ": output shape mismatch: " + attribute);
                            // Consume the worker's buffer in our own shader before the test-only readback.
                            float[] values = await CopyAndReadback(shader, inference.GetOutputBuffer(attribute), tensor.shape.length);
                            var comparison = Compare(attribute, expected.ReadFloats(), values, report.atol, report.rtol, report.angleDegrees);
                            result.attributes.Add(comparison);
                            result.passed &= comparison.passed;
                        }
                        result.wallMilliseconds = watch.Elapsed.TotalMilliseconds;
                        report.fixtures.Add(result);
                    }
                    finally { foreach (var input in inputs.Values) input.Dispose(); }
                }
                report.passed = report.fixtures.Count > 0 && report.fixtures.All(fixture => fixture.passed);
            }
            catch (Exception error) { report.error = error.ToString(); report.passed = false; }
            finally { EditorApplication.update -= EditorApplication.QueuePlayerLoopUpdate; }
            return report;
        }
    }
}
