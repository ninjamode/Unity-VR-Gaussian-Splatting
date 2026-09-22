using System.Reflection;
using Gaussians.FourD.Inference;
using NUnit.Framework;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEngine;

namespace Gaussians.FourD.Tests
{
    public sealed class Gaussian4DInferenceLifetimeTests
    {
        [Test]
        public void DisposingOneEvaluatorDoesNotReleaseSharedReaperOrAnotherWorker()
        {
            var asset=AssetDatabase.LoadAssetAtPath<GaussianSplat4DInferenceAsset>("Assets/Gaussians/4DGS/BouncingBalls/Inference.asset");
            if(!asset)Assert.Ignore("Requires the development ONNX bundle.");
            var shader=Object.Instantiate(AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/net.kleinbeck.gaussians/Shaders/4DGS/GaussianSplat4D.compute"));
            try
            {
                using var second=new GaussianSplat4DEvaluator(asset,shader);
                ComputeBuffer shared;
                var field=typeof(Worker).Assembly.GetType("Unity.InferenceEngine.ComputeTensorDataReaper")?.GetField("m_DummyDestination",BindingFlags.Static|BindingFlags.NonPublic);
                Assert.That(field,Is.Not.Null);
                using(var first=new GaussianSplat4DEvaluator(asset,shader))
                {
                    first.Evaluate(.2f);second.Evaluate(.3f);
                    shared=(ComputeBuffer)field.GetValue(null);Assert.That(shared.IsValid(),Is.True);
                }
                Assert.That(shared.IsValid(),Is.True,"A renderer must not dispose the package-global cleanup buffer.");
                Assert.That(second.Evaluate(.4f),Is.True);
                Assert.That(field.GetValue(null),Is.SameAs(shared),"Scheduling must not reinitialize resources shared with other workers.");
                Assert.That(shared.IsValid(),Is.True);
            }
            finally { Object.DestroyImmediate(shader); }
        }
    }
}
