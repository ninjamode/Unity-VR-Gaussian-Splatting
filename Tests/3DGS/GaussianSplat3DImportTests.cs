using System;
using System.IO;
using Gaussians.ThreeD.Editor.Utils;
using NUnit.Framework;
using UnityEngine;

namespace Gaussians.Package.Tests
{
    public sealed class GaussianSplat3DImportTests
    {
        string folder;
        [SetUp] public void SetUp()
        {
            folder = Path.Combine(Path.GetTempPath(), "gaussian-ply-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
        }
        [TearDown] public void TearDown() => Directory.Delete(folder, true);

        [Test] public void GeneratedPlyImportsGeometryActivationAndChannelOrderedSH()
        {
            string path = Path.Combine(folder, "cloud.ply");
            GeneratedGaussianCloud.WritePly(path);
            Assert.That(GaussianSplat3DFileReader.ReadFileHeader(path), Is.EqualTo(1000));
            GaussianSplat3DFileReader.ReadFile(path, out var splats);
            using (splats)
            {
                Assert.That(splats.Length, Is.EqualTo(1000));
                Assert.That(Vector3.Distance(splats[0].pos, new Vector3(-.465f, -.345f, 2)), Is.LessThan(1e-6));
                Assert.That(Vector3.Distance(splats[999].pos, new Vector3(39.99f, 0, 2)), Is.LessThan(1e-5));
                Assert.That(splats[0].opacity, Is.EqualTo(.8f).Within(1e-6));
                Assert.That(Vector3.Distance(splats[0].scale, Vector3.one * .02f), Is.LessThan(1e-6));
                Assert.That(splats[0].dc0, Is.EqualTo(Vector3.one * .5f));
                Assert.That(Vector3.Distance(splats[0].sh1, new Vector3(.001f, .016f, .031f)), Is.LessThan(1e-6));
                Assert.That(Vector3.Distance(splats[0].shF, new Vector3(.015f, .030f, .045f)), Is.LessThan(1e-6));
            }
        }

        [Test] public void TruncatedPlyCannotReturnPartialCloud()
        {
            string path = Path.Combine(folder, "truncated.ply");
            GeneratedGaussianCloud.WritePly(path, 2);
            using (var file = File.OpenWrite(path)) file.SetLength(file.Length - 4);
            Assert.Throws<IOException>(() => GaussianSplat3DFileReader.ReadFile(path, out _));
        }
    }
}
