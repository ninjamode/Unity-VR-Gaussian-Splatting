using System.Linq;
using System.Runtime.InteropServices;
using Gaussians.Core;
using Gaussians.ThreeD;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gaussians.Package.Tests
{
    public sealed class GaussianSplat3DRendererTests
    {
        GeneratedGaussianCloud cloud;
        GameObject root, cameraObject;
        GaussianSplat3DRenderer renderer;
        Camera camera;
        GaussianSplatCameraState.Scope cameraScope;
        [StructLayout(LayoutKind.Sequential)]
        struct View { public Vector4 Position; public Vector2 Axis1, Axis2; public uint RG, BA; }

        [OneTimeSetUp] public void CreateCloud()
        {
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("3DGS rendering requires compute shader support.");
            cloud = new GeneratedGaussianCloud();
        }
        [OneTimeTearDown] public void DeleteCloud() => cloud?.Dispose();

        [SetUp] public void SetUp()
        {
            root = new GameObject("Generated Gaussian renderer") { hideFlags = HideFlags.HideAndDontSave };
            root.SetActive(false);
            renderer = root.AddComponent<GaussianSplat3DRenderer>();
            renderer.m_Asset = cloud.Asset;
            renderer.m_SHOrder = 0;
            renderer.m_OptimizationOverrides = true;
            renderer.m_EarlyRejection = true;
            renderer.m_EarlyFrustumCulling = true;
            renderer.m_MinimumSplatRadiusPixels = 0;
            renderer.m_MinimumSplatDistance = 0;
            renderer.m_MinimumSplatOpacity = 0;
            renderer.m_AlphaCutoff = 0;
            renderer.m_CompactVisibleSplats = false;
            renderer.m_SortPrecision = GaussianSplat3DRenderer.SortPrecision.Bits32;
            renderer.m_SortNthFrame = 1;
            renderer.m_RenderPath = GaussianSplat3DRenderer.RenderPath.DirectTransparent;
            root.SetActive(true);
            cameraObject = new GameObject("Generated Gaussian camera") { hideFlags = HideFlags.HideAndDontSave };
            camera = cameraObject.AddComponent<Camera>(); camera.enabled = false;
            cameraScope = State(512).Apply(camera);
            Assert.That(renderer.HasValidRenderSetup, Is.True);
        }
        [TearDown] public void TearDown()
        {
            cameraScope.Dispose();
            if (root) Object.DestroyImmediate(root);
            if (cameraObject) Object.DestroyImmediate(cameraObject);
        }

        GaussianSplatCameraState State(int size)
        {
            var view = camera.worldToCameraMatrix;
            var projection = Matrix4x4.Perspective(60, 1, .1f, 100);
            return new GaussianSplatCameraState(view, projection, view, projection,
                new Vector2Int(size, size), new Vector2Int(size, size), 1);
        }

        [TestCase(16)] [TestCase(24)] [TestCase(32)]
        public void SortPrecisionPreservesDepthOrderAndStableTies(int bits)
        {
            renderer.m_SortPrecision = (GaussianSplat3DRenderer.SortPrecision)bits;
            var resources = renderer.GetCameraRenderResources(camera);
            using var cmd = new CommandBuffer();
            renderer.SortPoints(cmd, camera, Matrix4x4.identity, true, resources);
            Graphics.ExecuteCommandBuffer(cmd);
            var actual = new uint[GeneratedGaussianCloud.Count]; resources.GpuSortKeys.GetData(actual);
            var expected = Enumerable.Range(0, GeneratedGaussianCloud.Count)
                .OrderByDescending(i => GeneratedGaussianCloud.Position(i).z).Select(i => (uint)i).ToArray();
            CollectionAssert.AreEqual(expected, actual);
        }

        [Test] public void CompactionKeepsVisibleSplatsAndDisablingItRestoresAllIds()
        {
            var resources = renderer.GetCameraRenderResources(camera);
            void Prepare()
            {
                using var cmd = new CommandBuffer(); renderer.PrepareCamera(cmd, camera, true, resources);
                Graphics.ExecuteCommandBuffer(cmd);
            }
            renderer.m_CompactVisibleSplats = true; Prepare();
            var args = new uint[10]; resources.CompactArgs.GetData(args);
            Assert.That(args[0], Is.EqualTo(GeneratedGaussianCloud.VisibleCount));
            var order = new uint[GeneratedGaussianCloud.Count]; resources.GpuSortKeys.GetData(order);
            CollectionAssert.AreEqual(Enumerable.Range(0, GeneratedGaussianCloud.VisibleCount)
                    .OrderByDescending(i => GeneratedGaussianCloud.Position(i).z).Select(i => (uint)i),
                order.Take(GeneratedGaussianCloud.VisibleCount));
            renderer.m_CompactVisibleSplats = false; Prepare(); resources.GpuSortKeys.GetData(order);
            Assert.That(resources.Compacted, Is.False);
            CollectionAssert.AreEqual(Enumerable.Range(0, GeneratedGaussianCloud.Count)
                .OrderByDescending(i => GeneratedGaussianCloud.Position(i).z).Select(i => (uint)i), order);
        }

        [TestCase(GaussianSplat3DRenderer.StereoViewMode.PerEye)]
        [TestCase(GaussianSplat3DRenderer.StereoViewMode.EyeParallel)]
        [TestCase(GaussianSplat3DRenderer.StereoViewMode.SharedSource)]
        public void StereoProjectionUsesIndependentEyeMatrices(GaussianSplat3DRenderer.StereoViewMode mode)
        {
            renderer.m_StereoViewMode = mode;
            var state = State(512); var right = state.View; right.m03 -= .064f;
            using var scope = new GaussianSplatCameraState(state.View, state.Projection, right, state.Projection,
                state.ScreenSize, state.ScreenSize, 2).Apply(camera);
            var resources = renderer.GetCameraRenderResources(camera);
            using var cmd = new CommandBuffer(); renderer.CalcViewData(cmd, camera, resources);
            Graphics.ExecuteCommandBuffer(cmd);
            Assert.That(resources.PreparationStats.EffectiveMode, Is.EqualTo(mode));
            var views = new View[GeneratedGaussianCloud.Count * 2]; resources.GpuView.GetData(views);
            Assert.That(views[0].Position.w, Is.GreaterThan(0));
            Assert.That(views[GeneratedGaussianCloud.Count].Position.w, Is.GreaterThan(0));
            Assert.That(views[0].Position.x, Is.Not.EqualTo(views[GeneratedGaussianCloud.Count].Position.x));
            Assert.That(views[GeneratedGaussianCloud.Count].Position.x - views[0].Position.x,
                Is.EqualTo(state.Projection.m00 * -.064f).Within(1e-5));
            Assert.That(views[0].Position.z, Is.EqualTo(views[GeneratedGaussianCloud.Count].Position.z));
        }

        [Test] public void IndirectDrawMatchesFullDrawAndEmptyPopulationDrawsNothing()
        {
            var target = new RenderTexture(64, 64, 24, RenderTextureFormat.ARGB32);
            var readback = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            var previousTarget = RenderTexture.active;
            var previousScreen = Shader.GetGlobalVector("_ScreenParams");
            var previousProjection = Shader.GetGlobalVector("_ProjectionParams");
            try
            {
                target.Create();
                using var scope = State(64).Apply(camera);
                var resources = renderer.GetCameraRenderResources(camera);
                var properties = new MaterialPropertyBlock();
                renderer.SetAssetDataOnMaterial(properties);
                renderer.SetCameraProperties(properties, camera, resources);
                properties.SetBuffer(GaussianSplat3DRenderer.Props.SplatViewData, resources.GpuView);
                properties.SetBuffer(GaussianSplat3DRenderer.Props.OrderBuffer, resources.GpuSortKeys);
                Color32[] Render(bool compact)
                {
                    renderer.m_CompactVisibleSplats = compact;
                    using var cmd = new CommandBuffer(); renderer.PrepareCamera(cmd, camera, true, resources);
                    cmd.SetRenderTarget(target); cmd.ClearRenderTarget(true, true, Color.clear);
                    cmd.SetGlobalVector("_ScreenParams", new Vector4(64, 64, 1 + 1f / 64, 1 + 1f / 64));
                    cmd.SetGlobalVector("_ProjectionParams", new Vector4(1, .1f, 100, .01f));
                    if (compact)
                        cmd.DrawProceduralIndirect(renderer.m_GpuIndexBuffer, Matrix4x4.identity, renderer.m_MatSplatsDirect,
                            0, MeshTopology.Triangles, resources.CompactArgs, 20, properties);
                    else
                        cmd.DrawProcedural(renderer.m_GpuIndexBuffer, Matrix4x4.identity, renderer.m_MatSplatsDirect,
                            0, MeshTopology.Triangles, 6, GeneratedGaussianCloud.Count, properties);
                    Graphics.ExecuteCommandBuffer(cmd);
                    RenderTexture.active = target;
                    readback.ReadPixels(new Rect(0, 0, 64, 64), 0, 0); readback.Apply();
                    return readback.GetPixels32();
                }
                var full = Render(false);
                Assert.That(full.Any(pixel => pixel.a > 0), Is.True, "Generated cloud must produce visible pixels.");
                CollectionAssert.AreEqual(full, Render(true));
                renderer.m_MinimumSplatDistance = 1000;
                Assert.That(Render(true).All(pixel => pixel.a == 0), Is.True);
            }
            finally
            {
                RenderTexture.active = previousTarget;
                Shader.SetGlobalVector("_ScreenParams", previousScreen);
                Shader.SetGlobalVector("_ProjectionParams", previousProjection);
                Object.DestroyImmediate(readback); Object.DestroyImmediate(target);
            }
        }
    }
}
