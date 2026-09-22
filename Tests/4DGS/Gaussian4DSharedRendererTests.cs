using System;
using System.IO;
using System.Runtime.InteropServices;
using Gaussians.Core;
using Gaussians.ThreeD;
using Gaussians.FourD.Inference;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace Gaussians.FourD.Tests
{
    public sealed class Gaussian4DSharedRendererTests
    {
        const string Folder = "Assets/__GaussianSharedRendererTests";
        GameObject m_Object, m_CameraObject;
        GaussianSplat3DRenderer m_Renderer;
        GaussianSplat3DAsset m_Asset;
        Camera m_Camera;
        sealed class Source : IGaussianSplat3DDeformation, IDisposable
        {
            public readonly GaussianSplat3DData Data = new(3, 1);
            public uint Position = 1, Attributes = 1;
            public Source() { Data.Splats.SetData(Geometry()); Data.SH.SetData(new float[9]); }
            public bool TryGetFrame(GaussianSplat3DAsset asset, out GaussianSplat3DFrame frame)
            { frame = new GaussianSplat3DFrame(Data, Position, Attributes); return true; }
            public void Dispose() => Data.Dispose();
        }
        [StructLayout(LayoutKind.Sequential)]
        struct View { public Vector4 Position; public Vector2 Axis1, Axis2; public uint RG, BA; }
        static Vector4[] Geometry() => new[] {
            new Vector4(0,0,2,1), new Vector4(.03f,.01f,.02f,0), new Vector4(0,0,.38268343f,.92387953f),
            new Vector4(100,0,1,1), new Vector4(.01f,.01f,.01f,0), new Vector4(0,0,0,1),
            new Vector4(0,0,-1,1), new Vector4(.01f,.01f,.01f,0), new Vector4(0,0,0,1) };

        [SetUp] public void SetUp()
        {
            Assert.That(AssetDatabase.IsValidFolder(Folder), Is.False, "Refuse to overwrite an existing test folder.");
            Directory.CreateDirectory(Folder);
            var geometry = Geometry();
            var floats = new float[geometry.Length * 4];
            for (int i=0;i<geometry.Length;i++) for(int j=0;j<4;j++) floats[i*4+j]=geometry[i][j];
            var bytes = new byte[floats.Length*4]; Buffer.BlockCopy(floats,0,bytes,0,bytes.Length);
            File.WriteAllBytes(Folder+"/geometry.bytes",bytes); File.WriteAllBytes(Folder+"/sh.bytes",new byte[36]);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            m_Asset = ScriptableObject.CreateInstance<GaussianSplat3DAsset>();
            m_Asset.InitializeFloat(3,0,AssetDatabase.LoadAssetAtPath<TextAsset>(Folder+"/geometry.bytes"),
                AssetDatabase.LoadAssetAtPath<TextAsset>(Folder+"/sh.bytes"),new Bounds(Vector3.zero,Vector3.one*4),Hash128.Compute("fixture"));
            m_Object = new GameObject("Shared Gaussian test"); m_Object.SetActive(false);
            m_Renderer = m_Object.AddComponent<GaussianSplat3DRenderer>(); m_Renderer.m_Asset=m_Asset;
            m_Renderer.m_RenderPath=GaussianSplat3DRenderer.RenderPath.DirectTransparent;
            m_Object.SetActive(true);
            m_CameraObject=new GameObject("Shared Gaussian camera"); m_Camera=m_CameraObject.AddComponent<Camera>();
            m_Camera.enabled=false; m_Camera.nearClipPlane=.1f; m_Camera.farClipPlane=100;
        }
        [TearDown] public void TearDown()
        {
            if(m_Object) UnityEngine.Object.DestroyImmediate(m_Object);
            if(m_CameraObject) UnityEngine.Object.DestroyImmediate(m_CameraObject);
            if(m_Asset) UnityEngine.Object.DestroyImmediate(m_Asset);
            AssetDatabase.DeleteAsset(Folder);
        }

        [TestCase(false)] [TestCase(true)]
        public void SharedProjectionUsesFullCovarianceAndSortsLivePositions(bool orthographic)
        {
            using var source = new Source(); m_Renderer.SetDeformation(source);
            try
            {
                var view=m_Camera.worldToCameraMatrix;
                var projection=orthographic ? Matrix4x4.Ortho(-1,1,-1,1,.1f,100) : Matrix4x4.Perspective(60,1,.1f,100);
                using var scope=new GaussianSplatCameraState(view,projection,view,projection,new Vector2Int(512,512),new Vector2Int(512,512),1).Apply(m_Camera);
                m_Renderer.PrepareSource();
                var resources=m_Renderer.GetCameraRenderResources(m_Camera);
                using(var cmd=new CommandBuffer())
                {
                    m_Renderer.SortPoints(cmd,m_Camera,Matrix4x4.identity,true,resources);
                    m_Renderer.CalcViewData(cmd,m_Camera,resources);
                    Graphics.ExecuteCommandBuffer(cmd);
                }
                var result=new View[3]; resources.GpuView.GetData(result);
                Assert.That(result[0].Position.w,Is.GreaterThan(0));
                Assert.That(result[2].Position.w,Is.Zero,"Orthographic behind-camera splats must also be clipped");
                Assert.That(result[1].Axis1.magnitude,Is.LessThan(20),"Offscreen covariance must remain bounded");
                // Independent covariance oracle for the 45-degree rotated anisotropic first splat.
                var gpu=GL.GetGPUProjectionMatrix(projection,true);
                float fx=256*gpu.m00/(orthographic?1:2), fy=256*gpu.m11/(orthographic?1:2);
                float xx=2*(fx*fx*.0005f+.3f), yy=2*(fy*fy*.0005f+.3f), xy=2*fx*fy*.0004f;
                var a=result[0].Axis1; var b=result[0].Axis2;
                Assert.That(a.x*a.x+b.x*b.x,Is.EqualTo(xx).Within(.001));
                Assert.That(a.y*a.y+b.y*b.y,Is.EqualTo(yy).Within(.001));
                Assert.That(a.x*a.y+b.x*b.y,Is.EqualTo(xy).Within(.001),"Projected covariance orientation must retain its sign");
                var order=new uint[3];resources.GpuSortKeys.GetData(order);
                CollectionAssert.AreEqual(new uint[]{0,1,2},order);
            }
            finally { m_Renderer.SetDeformation(null); }
        }

        [Test] public void RevisionsInvalidateStationaryViewsAndOnlyPositionChangesForceSorting()
        {
            using var source=new Source();m_Renderer.SetDeformation(source);
            try
            {
                m_Renderer.PrepareSource();
                Assert.That(m_Renderer.ShouldPrepareViewForCamera(m_Camera),Is.True);
                Assert.That(m_Renderer.ShouldPrepareViewForCamera(m_Camera),Is.False);
                Assert.That(m_Renderer.ShouldSortForCamera(m_Camera,true),Is.True);
                Assert.That(m_Renderer.ShouldSortForCamera(m_Camera,true),Is.False);
                source.Attributes++;m_Renderer.PrepareSource();
                Assert.That(m_Renderer.ShouldPrepareViewForCamera(m_Camera),Is.True);
                Assert.That(m_Renderer.ShouldSortForCamera(m_Camera,true),Is.False);
                source.Position++;source.Attributes++;m_Renderer.PrepareSource();
                Assert.That(m_Renderer.ShouldPrepareViewForCamera(m_Camera),Is.True);
                Assert.That(m_Renderer.ShouldSortForCamera(m_Camera,true),Is.True);
                m_Renderer.SetDeformation(null);m_Renderer.PrepareSource();
                Assert.That(m_Renderer.IsDeformed,Is.False);
                Assert.That(m_Renderer.HasValidRenderSetup,Is.True);
                Assert.That(m_Renderer.ShouldPrepareViewForCamera(m_Camera),Is.True);
            }
            finally { m_Renderer.SetDeformation(null); }
        }

        [Test] public void StereoPreparesIndependentEyeViewsFromOneSource()
        {
            using var source=new Source();m_Renderer.SetDeformation(source);
            try
            {
                var left=m_Camera.worldToCameraMatrix;var right=left;right.m03=-.064f;
                var projection=Matrix4x4.Perspective(60,1,.1f,100);
                using var scope=new GaussianSplatCameraState(left,projection,right,projection,new Vector2Int(512,512),new Vector2Int(512,512),2).Apply(m_Camera);
                m_Renderer.PrepareSource();var resources=m_Renderer.GetCameraRenderResources(m_Camera);
                Assert.That(resources.GpuView.count,Is.EqualTo(6));
                using(var cmd=new CommandBuffer()) { m_Renderer.CalcViewData(cmd,m_Camera,resources);Graphics.ExecuteCommandBuffer(cmd); }
                var views=new View[6];resources.GpuView.GetData(views);
                Assert.That(views[0].Position.x,Is.Not.EqualTo(views[3].Position.x));
                Assert.That(views[0].Position.z,Is.EqualTo(views[3].Position.z));
            }
            finally { m_Renderer.SetDeformation(null); }
        }

        void UsePackedAsset(bool chunked)
        {
            TextAsset File(string name, byte[] bytes)
            {
                string path=Folder+"/"+name+".bytes";System.IO.File.WriteAllBytes(path,bytes);
                AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport);
                return AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            }
            byte[] Floats(params float[] values) { var bytes=new byte[values.Length*4];Buffer.BlockCopy(values,0,bytes,0,bytes.Length);return bytes; }
            var position=File("packed-pos",Floats(0,0,2, 100,0,1, 0,0,-1));
            var otherBytes=new byte[3*16];
            for(int i=0;i<3;i++)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(512u|(512u<<10)|(512u<<20)|(3u<<30)),0,otherBytes,i*16,4);
                Buffer.BlockCopy(Floats(.01f,.01f,.01f),0,otherBytes,i*16+4,12);
            }
            var other=File("packed-other",otherBytes);
            var color=File("packed-color",new byte[2048*16*16]);
            var sh=File("packed-sh",new byte[3*192]);
            TextAsset chunks=null;
            if(chunked)
            {
                var bytes=new byte[64];Buffer.BlockCopy(Floats(10,20,10,20,10,20),0,bytes,16,24);
                chunks=File("chunks",bytes);
            }
            m_Renderer.enabled=false;UnityEngine.Object.DestroyImmediate(m_Asset);
            m_Asset=ScriptableObject.CreateInstance<GaussianSplat3DAsset>();
            m_Asset.Initialize(3,GaussianSplat3DAsset.VectorFormat.Float32,GaussianSplat3DAsset.VectorFormat.Float32,
                GaussianSplat3DAsset.ColorFormat.Float32x4,GaussianSplat3DAsset.SHFormat.Float32,Vector3.zero,Vector3.one,null);
            m_Asset.SetAssetFiles(chunks,position,other,color,sh);
            m_Renderer.m_Asset=m_Asset;m_Renderer.enabled=true;
        }

        [Test] public void PackedResizeRetainsValidBindings()
        {
            UsePackedAsset(false);
            m_Renderer.EditSetSplatCount(4);
            Assert.That(m_Renderer.splatCount,Is.EqualTo(4));
            var resources=m_Renderer.GetCameraRenderResources(m_Camera);
            using var cmd=new CommandBuffer();
            Assert.DoesNotThrow(()=>m_Renderer.CalcViewData(cmd,m_Camera,resources));
            Graphics.ExecuteCommandBuffer(cmd);
            var views=new View[4];resources.GpuView.GetData(views);
        }

        [Test] public void LivePositionsBypassPackedChunkDecoding()
        {
            UsePackedAsset(true);
            using var source=new Source();m_Renderer.SetDeformation(source);
            try
            {
                m_Renderer.PrepareSource();var resources=m_Renderer.GetCameraRenderResources(m_Camera);
                using var cmd=new CommandBuffer();
                m_Renderer.SortPoints(cmd,m_Camera,Matrix4x4.identity,true,resources);
                Graphics.ExecuteCommandBuffer(cmd);
                var keys=new uint[3];resources.GpuSortDistances.GetData(keys);
                // Translation in chunk bounds must not alter the metric of already activated positions.
                var original=(uint[])keys.Clone();
                cmd.Clear();
                m_Renderer.m_GpuChunksValid=false;
                m_Renderer.SortPoints(cmd,m_Camera,Matrix4x4.identity,true,resources);
                Graphics.ExecuteCommandBuffer(cmd);resources.GpuSortDistances.GetData(keys);
                CollectionAssert.AreEqual(original,keys);
            }
            finally { m_Renderer.SetDeformation(null); }
        }

        [Test] public void CorrectingCompatibilityAndTimeResumesEvaluationAndDisableRestoresCanonical()
        {
            var asset=AssetDatabase.LoadAssetAtPath<GaussianSplat4DInferenceAsset>("Assets/Gaussians/4DGS/BouncingBalls/Inference.asset");
            if(!asset || !asset.Canonical) Assert.Ignore("Requires the imported development bundle.");
            m_Renderer.enabled=false;m_Renderer.m_Asset=asset.Canonical;m_Renderer.enabled=true;
            var addon=m_Object.AddComponent<GaussianSplat4D>();addon.m_Asset=asset;addon.enabled=false;
            Assert.That(addon.TryGetFrame(asset.Canonical,out _),Is.False);
            addon.enabled=true;
            LogAssert.Expect(LogType.Warning,"4D deformation rejected: assign its unchanged canonical model to the 3D renderer.");
            Assert.That(addon.TryGetFrame(m_Asset,out _),Is.False);
            Assert.That(addon.TryGetFrame(asset.Canonical,out var frame),Is.True,"Compatibility must recover without manual rebuild");
            int evaluations=addon.EvaluationCount;
            Assert.That(addon.TryGetFrame(asset.Canonical,out _),Is.True);
            Assert.That(addon.EvaluationCount,Is.EqualTo(evaluations));
            addon.m_ModelTime=float.NaN;
            Assert.That(addon.TryGetFrame(asset.Canonical,out _),Is.False);
            addon.m_ModelTime=.5f;
            Assert.That(addon.TryGetFrame(asset.Canonical,out _),Is.True);
            m_Renderer.PrepareSource();Assert.That(m_Renderer.IsDeformed,Is.True);
            addon.enabled=false;m_Renderer.PrepareSource();
            Assert.That(m_Renderer.IsDeformed,Is.False);
            Assert.That(m_Renderer.HasValidRenderSetup,Is.True);
            Assert.That(frame.Data.Splats,Is.Null,"Disabled add-on releases its owned buffers");
        }

        [Test] public void AddonRejectsUnrelatedAssetAndChangedIdentityWithoutHidingCanonical()
        {
            var inference=ScriptableObject.CreateInstance<GaussianSplat4DInferenceAsset>();
            var data=ScriptableObject.CreateInstance<GaussianSplat4DAsset>();
            var other=UnityEngine.Object.Instantiate(m_Asset);
            try
            {
                data.Initialize(3,0,2,null,Array.Empty<GaussianTensorData>());
                inference.Initialize(data,null);inference.SetCanonical(m_Asset);
                Assert.That(inference.Matches(m_Asset),Is.True);
                Assert.That(inference.Matches(other),Is.False,"Equal count/hash is insufficient for an unrelated asset");
                var addon=m_Object.AddComponent<GaussianSplat4D>();addon.m_Asset=inference;
                m_Renderer.m_Asset=other;
                LogAssert.Expect(LogType.Warning,"4D deformation rejected: assign its unchanged canonical model to the 3D renderer.");
                Assert.That(addon.TryGetFrame(other,out _),Is.False);
                Assert.That(addon.EvaluationCount,Is.Zero);
                Assert.That(m_Renderer.HasValidAsset,Is.True);
                m_Asset.SetDataHash(Hash128.Compute("edited"));
                Assert.That(inference.Matches(m_Asset),Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(inference);UnityEngine.Object.DestroyImmediate(data);UnityEngine.Object.DestroyImmediate(other); }
        }
    }
}
