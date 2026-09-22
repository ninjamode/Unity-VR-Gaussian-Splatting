using System;
using System.IO;
using System.Linq;
using Gaussians.FourD.Editor;
using Google.Protobuf;
using NUnit.Framework;

namespace Gaussians.FourD.Tests
{
    public sealed class Gaussian4DValidationTests
    {
        [Test]
        public void NonfiniteOrInvalidShapeCannotPass()
        {
            Assert.IsFalse(GaussianSplat4DValidation.Compare("positions", new[]{0f}, new[]{float.NaN}, 1e-5, 1e-4, 0.1).passed);
            Assert.IsFalse(GaussianSplat4DValidation.Compare("positions", Array.Empty<float>(), Array.Empty<float>(), 1e-5, 1e-4, 0.1).passed);
            Assert.IsFalse(GaussianSplat4DValidation.Compare("rotations_wxyz", new[]{1f}, new[]{1f}, 1e-5, 1e-4, 0.1).passed);
        }

        [Test]
        public void QuaternionAngularErrorAndZeroNormAreChecked()
        {
            Assert.IsFalse(GaussianSplat4DValidation.Compare("rotations_wxyz", new float[4], new float[4], 1e-5, 1e-4, 0.1).passed);
            var result = GaussianSplat4DValidation.Compare("rotations_wxyz", new[]{2f,0f,0f,0f}, new[]{2f,0.1f,0f,0f}, 1, 0, 0.1);
            Assert.AreEqual(0, result.failedElements); // Raw values pass the deliberately loose threshold.
            Assert.Greater(result.quaternionMaxAngleDegrees, 5);
            Assert.IsFalse(result.passed);
        }

        [Test]
        public void ActivatedScaleCanFailWhenRawLogScalePasses()
        {
            var result = GaussianSplat4DValidation.Compare("log_scales", new[]{0f}, new[]{1f}, 1.1, 0, 0.1);
            Assert.AreEqual(0, result.failedElements);
            Assert.Greater(result.activatedMaxAbsolute, 1.7);
            Assert.IsFalse(result.passed);
        }

        [Test]
        public void PathTraversalIsRejected()
        {
            Assert.Throws<InvalidDataException>(() => GaussianSplat4DBundleImporter.CheckedPath(Path.GetTempPath(), "../outside"));
        }

        [TestCase(-1, false, false, 1)]
        [TestCase(0, false, false, 0)]
        [TestCase(-1, true, false, 0)]
        [TestCase(-1, false, true, 0)]
        public void SqueezeRewriteRequiresSoleIndependentReshape(int firstDimension, bool extraConsumer, bool graphOutput, int expectedChanges)
        {
            string root = Path.Combine(Path.GetTempPath(), "gaussian-onnx-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string json = "{\"graph\":{\"node\":[" +
                    "{\"opType\":\"Squeeze\",\"input\":[\"x\"],\"output\":[\"s\"]}," +
                    "{\"opType\":\"Constant\",\"output\":[\"shape\"],\"attribute\":[{\"name\":\"value\",\"t\":{\"dims\":[\"2\"],\"dataType\":7,\"int64Data\":[\"" + firstDimension + "\",\"4\"]}}]}," +
                    "{\"opType\":\"Reshape\",\"input\":[\"s\",\"shape\"],\"output\":[\"y\"]}" +
                    (extraConsumer ? ",{\"opType\":\"Identity\",\"input\":[\"s\"],\"output\":[\"z\"]}" : "") +
                    "],\"output\":[{\"name\":\"" + (graphOutput ? "s" : "y") + "\"}]}}";
                var assembly = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Unity.InferenceEngine.Editor");
                var type = assembly.GetType("Unity.InferenceEngine.Editor.Onnx.ModelProto", true);
                var descriptor = (Google.Protobuf.Reflection.MessageDescriptor)type.GetProperty("Descriptor").GetValue(null);
                var model = JsonParser.Default.Parse(json, descriptor);
                string source = Path.Combine(root, "original.onnx"), destination = Path.Combine(root, "compatible.onnx");
                File.WriteAllBytes(source, model.ToByteArray());
                byte[] original = File.ReadAllBytes(source);
                Assert.AreEqual(expectedChanges, Gaussian4DOnnxCompatibility.Prepare(source, destination));
                CollectionAssert.AreEqual(original, File.ReadAllBytes(source));
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
