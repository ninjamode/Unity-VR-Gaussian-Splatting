using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Gaussians.FourD.Tests
{
    public sealed class Gaussian4DNativeTests
    {
        const string ShaderRoot = "Packages/net.kleinbeck.gaussians/Shaders/4DGS/";
        static readonly string[] Attributes = { "positions", "log_scales", "rotations_wxyz", "opacity_logits", "sh" };
        static readonly string[] Heads = { "pos_deform", "scales_deform", "rotations_deform", "opacity_deform", "shs_deform" };
        static readonly int[] Widths = { 3, 3, 4, 1, 48 };
        static readonly int[,] Axes = { { 0, 1 }, { 0, 2 }, { 0, 3 }, { 1, 2 }, { 1, 3 }, { 2, 3 } };

        [TestCase(31, false, false)]
        [TestCase(31, true, false)]
        [TestCase(31, true, true)] // Tiny nonzero Hamilton delta is normalized before activation.
        [TestCase(10, false, false)] // Only scale and opacity; SH must remain canonical across evaluations.
        [TestCase(21, true, false)] // Position, rotation and SH; scale and opacity must pass through.
        [TestCase(0, true, false)]
        public void SyntheticNativeMatchesIndependentPortableOracle(int activeHeads, bool applyRotation, bool tinyRotation)
        {
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("Requires compute shader support.");
            var nativeShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderRoot + "NativeDeformation.compute");
            var activationShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderRoot + "GaussianSplat4D.compute");
            Assert.That(nativeShader, Is.Not.Null);
            Assert.That(activationShader, Is.Not.Null);
            using var fixture = new SyntheticBundle(activeHeads, applyRotation, tinyRotation);
            using var native = new GaussianSplat4DNative(fixture.Asset, nativeShader, activationShader);
            // Includes endpoint and out-of-border time samples, repeated times, and canonical toggles.
            foreach (float time in new[] { -1.6f, -1f, -0.35f, 0.6f, 1f, 1.4f, -0.35f })
            {
                native.Evaluate(time, true);
                AssertOutput(fixture, native, time, true);
            }
            native.Evaluate(0.6f, false);
            AssertOutput(fixture, native, 0.6f, false);
            native.Evaluate(0.6f, true);
            AssertOutput(fixture, native, 0.6f, true);
            native.Evaluate(-1f, false);
            AssertOutput(fixture, native, -1f, false);
        }

        [TestCase(31)]
        [TestCase(10)]
        public void NativePackedSHMatchesFP32AndPreservesGeometry(int activeHeads)
        {
            using var fixture = new SyntheticBundle(activeHeads, false, false);
            var nativeShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderRoot + "NativeDeformation.compute");
            var activationShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderRoot + "GaussianSplat4D.compute");
            using var reference = new GaussianSplat4DNative(fixture.Asset,nativeShader,activationShader);
            using var compact = new GaussianSplat4DNative(fixture.Asset,nativeShader,activationShader,Gaussians.ThreeD.GaussianSplatSHStorage.Float16);
            var expected = new float[SyntheticBundle.Count*48];var actual = new uint[SyntheticBundle.Count*24];
            var a=new Vector4[SyntheticBundle.Count*3];var b=new Vector4[a.Length];
            foreach(float time in new[]{0f,.37f,1f,.37f})
            {
                reference.Evaluate(time,true);compact.Evaluate(time,true);
                reference.Data.Splats.GetData(a);compact.Data.Splats.GetData(b);CollectionAssert.AreEqual(a,b);
                reference.Data.SH.GetData(expected);compact.Data.SH.GetData(actual);
                for(int i=0;i<expected.Length;i++)
                    Assert.That(Mathf.HalfToFloat((ushort)(actual[i/2]>>((i&1)*16))),Is.EqualTo(expected[i]).Within(0.0005*Math.Abs(expected[i])+0.000001));
            }
            Assert.That(compact.Data.SH.count,Is.EqualTo(reference.Data.SH.count/2));
        }

        static void AssertOutput(SyntheticBundle fixture, GaussianSplat4DNative native, float time, bool deform)
        {
            var splats = new Vector4[SyntheticBundle.Count * 3];
            var sh = new float[SyntheticBundle.Count * 48];
            native.Data.Splats.GetData(splats);
            native.Data.SH.GetData(sh);
            for (int point = 0; point < SyntheticBundle.Count; point++)
            {
                float[][] expected = fixture.Evaluate(point, time, deform);
                string context = $"point={point}, time={time}, deform={deform}";
                for (int axis = 0; axis < 3; axis++)
                {
                    Near(expected[0][axis], splats[point * 3][axis], context + " position " + axis);
                    Near(Mathf.Exp(expected[1][axis]), splats[point * 3 + 1][axis], context + " scale " + axis);
                }
                Near(1f / (1f + Mathf.Exp(-expected[3][0])), splats[point * 3].w, context + " opacity");
                float[] q = expected[2];
                double norm = Math.Sqrt((double)q[0] * q[0] + (double)q[1] * q[1] + (double)q[2] * q[2] + (double)q[3] * q[3]);
                for (int axis = 0; axis < 4; axis++)
                    Near((float)(q[(axis + 1) % 4] / norm), splats[point * 3 + 2][axis], context + " rotation " + axis);
                Near(0, splats[point * 3 + 1].w, context + " scale padding");
                for (int coefficient = 0; coefficient < 48; coefficient++)
                    Near(expected[4][coefficient], sh[point * 48 + coefficient], context + " SH " + coefficient);
            }
        }

        static void Near(float expected, float actual, string context) =>
            Assert.That(actual, Is.EqualTo(expected).Within(1e-5 + 1e-4 * Math.Abs(expected)), context);

        // Scalar CPU oracle follows model.py directly: all six planes in combination order,
        // border/align_corners bilinear sampling, input ReLUs, then canonical composition.
        // It deliberately does not share the native shader's cached spatial products or tiled loops.
        internal sealed class SyntheticBundle : IDisposable
        {
            public const int Count = 11; // Partial eight-point dense tile and partial 64-thread kernels.
            const int Channels = 9, Levels = 2;
            readonly Dictionary<string, float[]> m_Values = new();
            readonly Dictionary<string, int[]> m_Shapes = new();
            readonly List<GaussianTensorData> m_Tensors = new();
            readonly int m_ActiveHeads;
            readonly bool m_ApplyRotation;
            readonly bool m_TinyRotation;
            TextAsset m_Manifest;
            public GaussianSplat4DAsset Asset { get; private set; }

            public SyntheticBundle(int activeHeads, bool applyRotation, bool tinyRotation)
            {
                m_ActiveHeads = activeHeads;
                m_ApplyRotation = applyRotation;
                m_TinyRotation = tinyRotation;
                try
                {
                    BuildTensors();
                    string Flag(int head) => (activeHeads & (1 << head)) == 0 ? "true" : "false";
                    string configuration = "\"multires\":[1,2],\"grid_pe\":0,\"no_grid\":false,\"static_mlp\":false,\"empty_voxel\":false," +
                        "\"no_dx\":" + Flag(0) + ",\"no_ds\":" + Flag(1) + ",\"no_dr\":" + Flag(2) +
                        ",\"no_do\":" + Flag(3) + ",\"no_dshs\":" + Flag(4) + ",\"apply_rotation\":" + (applyRotation ? "true" : "false");
                    var canonical = new List<string>();
                    foreach (string attribute in Attributes) canonical.Add("\"" + attribute + "\":\"canonical/" + attribute + "\"");
                    m_Manifest = new TextAsset("{\"configuration\":{" + configuration + "},\"canonical\":{" + string.Join(",", canonical) + "}}");
                    Asset = ScriptableObject.CreateInstance<GaussianSplat4DAsset>();
                    Asset.Initialize(Count, 3, 2, m_Manifest, m_Tensors.ToArray());
                }
                catch { Dispose(); throw; }
            }

            void Add(string name, int[] shape, float[] values)
            {
                m_Values.Add(name, values);
                m_Shapes.Add(name, shape);
                var bytes = new byte[values.Length * sizeof(float)];
                Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
                m_Tensors.Add(new GaussianTensorData {
                    name = name, dtype = "<f4", shape = shape,
                    data = new TextAsset(new ReadOnlySpan<byte>(bytes))
                });
            }

            void BuildTensors()
            {
                // Anisotropic, asymmetric AABB, both endpoints and points beyond every axis.
                float[] positions = { 2, 3, 4, -2, -1, -3, 0, 1, 0.5f,
                    3, -2, 5, -3, 4, -4, 0.7f, -0.2f, 2.3f,
                    -1.5f, 2.6f, -2.2f, 2, -1, 4, -2, 3, -3,
                    1.25f, 0.25f, -0.75f, -0.4f, 1.9f, 3.1f };
                Add("canonical/positions", new[] { Count, 3 }, positions);
                for (int head = 1; head < 5; head++)
                {
                    var values = new float[Count * Widths[head]];
                    for (int i = 0; i < values.Length; i++) values[i] = 0.3f * Mathf.Sin(i * 0.43f + head);
                    if (head == 2)
                    {
                        if (m_TinyRotation) Array.Clear(values, 0, values.Length);
                        for (int i = 0; i < Count; i++) values[i * 4] += m_TinyRotation ? 1f : 1.2f;
                    }
                    Add("canonical/" + Attributes[head], head == 4 ? new[] { Count, 16, 3 } : new[] { Count, Widths[head] }, values);
                }
                Add("weights/deformation_net.grid.aabb", new[] { 2, 3 }, new float[] { 2, 3, 4, -2, -1, -3 });
                for (int level = 0; level < Levels; level++)
                    for (int plane = 0; plane < 6; plane++)
                    {
                        int height = 3 + level + plane % 2, width = 5 + level * 2 + plane % 3;
                        var values = new float[Channels * height * width];
                        for (int channel = 0; channel < Channels; channel++)
                            for (int y = 0; y < height; y++)
                                for (int x = 0; x < width; x++)
                                {
                                    float value = 0.68f + 0.025f * channel + 0.018f * plane + 0.035f * level +
                                        0.021f * x - 0.017f * y + 0.007f * x * y;
                                    if (plane == 0 && channel % 3 == 0) value = -value;
                                    values[(channel * height + y) * width + x] = value;
                                }
                        Add("weights/deformation_net.grid.grids." + level + "." + plane, new[] { 1, Channels, height, width }, values);
                    }
                // All hidden dimensions cross a 16-wide tile and have a partial tail.
                Layer("feature_out.0", Channels * Levels, 19, 1);
                Layer("feature_out.2", 19, 17, 2);
                for (int head = 0; head < 5; head++)
                {
                    // Disabled heads are absent, ensuring the runtime really honors the flags.
                    if ((m_ActiveHeads & (1 << head)) == 0) continue;
                    Layer(Heads[head] + ".1", 17, 21, 3 + head);
                    Layer(Heads[head] + ".3", 21, Widths[head], 8 + head);
                }
            }

            void Layer(string name, int input, int output, int seed)
            {
                var weights = new float[input * output];
                var biases = new float[output];
                for (int row = 0; row < output; row++)
                {
                    biases[row] = 0.12f * Mathf.Cos(row * 0.71f + seed);
                    for (int col = 0; col < input; col++)
                        weights[row * input + col] = 0.19f * Mathf.Sin(row * 0.37f + col * 0.83f + seed * 0.51f);
                }
                if (m_TinyRotation && name == "rotations_deform.3")
                {
                    Array.Clear(weights, 0, weights.Length);
                    for (int i = 0; i < output; i++) biases[i] = (i + 1) * 1e-13f;
                }
                Add("weights/deformation_net." + name + ".weight", new[] { output, input }, weights);
                Add("weights/deformation_net." + name + ".bias", new[] { output }, biases);
            }

            float Sample(string name, int channel, float u, float v)
            {
                int height = m_Shapes[name][2], width = m_Shapes[name][3];
                float x = Mathf.Clamp((u + 1) * (width - 1) / 2, 0, width - 1);
                float y = Mathf.Clamp((v + 1) * (height - 1) / 2, 0, height - 1);
                int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
                int x1 = Math.Min(x0 + 1, width - 1), y1 = Math.Min(y0 + 1, height - 1);
                float fx = x - x0, fy = y - y0;
                float At(int xx, int yy) => m_Values[name][(channel * height + yy) * width + xx];
                return (1 - fy) * ((1 - fx) * At(x0, y0) + fx * At(x1, y0)) +
                    fy * ((1 - fx) * At(x0, y1) + fx * At(x1, y1));
            }

            float[] Linear(float[] input, string name, bool reluInput)
            {
                string prefix = "weights/deformation_net." + name;
                float[] weights = m_Values[prefix + ".weight"], bias = m_Values[prefix + ".bias"];
                var output = new float[bias.Length];
                for (int row = 0; row < output.Length; row++)
                {
                    float value = bias[row];
                    for (int col = 0; col < input.Length; col++)
                        value += weights[row * input.Length + col] * (reluInput ? Math.Max(0, input[col]) : input[col]);
                    output[row] = value;
                }
                return output;
            }

            public float[][] Evaluate(int point, float time, bool deform)
            {
                var output = new float[5][];
                for (int head = 0; head < 5; head++)
                {
                    output[head] = new float[Widths[head]];
                    Array.Copy(m_Values["canonical/" + Attributes[head]], point * Widths[head], output[head], 0, Widths[head]);
                }
                if (!deform || m_ActiveHeads == 0) return output;
                var coordinates = new float[4];
                float[] aabb = m_Values["weights/deformation_net.grid.aabb"];
                for (int axis = 0; axis < 3; axis++)
                    coordinates[axis] = (output[0][axis] - aabb[axis]) * (2f / (aabb[axis + 3] - aabb[axis])) - 1;
                coordinates[3] = time;
                var features = new float[Channels * Levels];
                for (int level = 0; level < Levels; level++)
                    for (int channel = 0; channel < Channels; channel++)
                    {
                        float product = 1;
                        for (int plane = 0; plane < 6; plane++)
                            product *= Sample("weights/deformation_net.grid.grids." + level + "." + plane, channel,
                                coordinates[Axes[plane, 0]], coordinates[Axes[plane, 1]]);
                        features[level * Channels + channel] = product;
                    }
                float[] hidden = Linear(Linear(features, "feature_out.0", false), "feature_out.2", true);
                for (int head = 0; head < 5; head++)
                {
                    if ((m_ActiveHeads & (1 << head)) == 0) continue;
                    float[] delta = Linear(Linear(hidden, Heads[head] + ".1", true), Heads[head] + ".3", true);
                    if (head == 2 && m_ApplyRotation)
                    {
                        float[] a = output[head], b = delta;
                        output[head] = new[] {
                            a[0]*b[0]-a[1]*b[1]-a[2]*b[2]-a[3]*b[3],
                            a[0]*b[1]+a[1]*b[0]+a[2]*b[3]-a[3]*b[2],
                            a[0]*b[2]-a[1]*b[3]+a[2]*b[0]+a[3]*b[1],
                            a[0]*b[3]+a[1]*b[2]-a[2]*b[1]+a[3]*b[0] };
                        // model.py normalizes Hamilton output; activation normalizes again.
                        float norm = 0;
                        foreach (float value in output[head]) norm += value * value;
                        norm = Mathf.Sqrt(norm);
                        for (int i = 0; i < 4; i++) output[head][i] /= norm;
                    }
                    else for (int i = 0; i < delta.Length; i++) output[head][i] += delta[i];
                }
                return output;
            }

            public void Dispose()
            {
                if (Asset) UnityEngine.Object.DestroyImmediate(Asset);
                if (m_Manifest) UnityEngine.Object.DestroyImmediate(m_Manifest);
                foreach (var tensor in m_Tensors)
                    if (tensor.data) UnityEngine.Object.DestroyImmediate(tensor.data);
                m_Tensors.Clear();
            }
        }
    }
}
