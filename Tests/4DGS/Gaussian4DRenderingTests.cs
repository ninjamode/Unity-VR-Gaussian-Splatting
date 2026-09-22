using System;
using System.Collections.Generic;
using Gaussians.Core;
using Gaussians.FourD.Inference;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Gaussians.FourD.Tests
{
    public sealed class Gaussian4DRenderingTests
    {
        static ComputeShader Shader() => UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<ComputeShader>(
            "Packages/net.kleinbeck.gaussians/Shaders/4DGS/GaussianSplat4D.compute"));

        [Test]
        public void GpuActivationPreservesLayoutAndRejectsInvalidRotation()
        {
            var shader = Shader();
            var raw = new Dictionary<string,ComputeBuffer>();
            try
            {
                void Add(string name, float[] values)
                {
                    var buffer = new ComputeBuffer(values.Length,4);
                    buffer.SetData(values); raw.Add(name,buffer);
                }
                Add("positions",new float[] {1,2,3, 4,5,6});
                Add("log_scales",new[] {Mathf.Log(2),Mathf.Log(3),Mathf.Log(4), 0,0,0});
                Add("rotations_wxyz",new float[] {2,0,0,2, 0,0,0,0});
                Add("opacity_logits",new float[] {0,0});
                Add("sh",new float[] {10,20,30, 40,50,60});
                using var data = new GaussianSplat4DGpuData(2,1,shader);
                data.Apply(raw);
                var packed = new Vector4[6]; data.Splats.GetData(packed);
                Assert.That(packed[0],Is.EqualTo(new Vector4(1,2,3,0.5f)));
                Assert.That(Vector4.Distance(packed[1],new Vector4(2,3,4,0)),Is.LessThan(1e-5));
                Assert.That(Vector4.Distance(packed[2],new Vector4(0,0,Mathf.Sqrt(0.5f),Mathf.Sqrt(0.5f))),Is.LessThan(1e-5));
                Assert.That(packed[3].w,Is.Zero,"Invalid rotation must not draw");
                var sh = new float[6]; data.SH.GetData(sh);
                CollectionAssert.AreEqual(new float[] {10,20,30,40,50,60},sh);
            }
            finally { foreach(var buffer in raw.Values) buffer.Dispose(); UnityEngine.Object.DestroyImmediate(shader); }
        }

        [TestCase("BouncingBalls", false)]
        [TestCase("Room", false)]
        [TestCase("BouncingBalls", true)]
        [TestCase("Room", true)]
        public void FullCloudEvaluationMatchesFixturesAndDoesNotAccumulate(string scene, bool native)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GaussianSplat4DInferenceAsset>("Assets/Gaussians/4DGS/"+scene+"/Inference.asset");
            if (!asset) Assert.Ignore("Requires the development project's imported " + scene + " bundle.");
            var shader = Shader();
            try
            {
                var nativeShader = native ? AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Packages/net.kleinbeck.gaussians/Shaders/4DGS/NativeDeformation.compute") : null;
                using var evaluator = new GaussianSplat4DEvaluator(asset,shader,nativeShader);
                foreach (int fixture in new[] {0,1,2,3,4,0})
                {
                    string prefix="fixtures/sample_"+fixture+"/";
                    float time=asset.Data.GetTensor(prefix+"input_times").ReadFloats()[0];
                    Assert.That(evaluator.Evaluate(time),Is.True);
                    Assert.That(evaluator.Evaluate(time),Is.False,"Same time must reuse GPU results");
                    var packed = new Vector4[asset.Data.GaussianCount*3]; evaluator.Data.Splats.GetData(packed);
                    var actualSH = new float[asset.Data.GaussianCount*evaluator.Data.CoefficientCount*3]; evaluator.Data.SH.GetData(actualSH);
                    byte[] indices=asset.Data.GetTensor(prefix+"indices").data.bytes;
                    var positions=asset.Data.GetTensor(prefix+"output_positions").ReadFloats();
                    var scales=asset.Data.GetTensor(prefix+"output_log_scales").ReadFloats();
                    var rotations=asset.Data.GetTensor(prefix+"output_rotations_wxyz").ReadFloats();
                    var opacity=asset.Data.GetTensor(prefix+"output_opacity_logits").ReadFloats();
                    var sh=asset.Data.GetTensor(prefix+"output_sh").ReadFloats();
                    for(int i=0;i<indices.Length/4;i++)
                    {
                        int index=BitConverter.ToInt32(indices,i*4);
                        for(int k=0;k<3;k++)
                        {
                            Near(positions[i*3+k],packed[index*3][k]);
                            Near(Mathf.Exp(scales[i*3+k]),packed[index*3+1][k]);
                        }
                        Near(1f/(1f+Mathf.Exp(-opacity[i])),packed[index*3].w);
                        var q=new Vector4(rotations[i*4+1],rotations[i*4+2],rotations[i*4+3],rotations[i*4]).normalized;
                        Assert.That(Mathf.Abs(Vector4.Dot(q,packed[index*3+2])),Is.GreaterThan(0.99999f));
                        for(int k=0;k<evaluator.Data.CoefficientCount*3;k++) Near(sh[i*evaluator.Data.CoefficientCount*3+k],actualSH[index*evaluator.Data.CoefficientCount*3+k]);
                    }
                }
                Assert.That(evaluator.EvaluationCount,Is.EqualTo(6));
                evaluator.Evaluate(0,false);
                var canonical=new Vector4[asset.Data.GaussianCount*3]; evaluator.Data.Splats.GetData(canonical);
                var p=asset.Data.GetTensor("canonical/positions").ReadFloats();
                for(int i=0;i<asset.Data.GaussianCount;i++) for(int k=0;k<3;k++) Near(p[i*3+k],canonical[i*3][k]);
                Assert.That(evaluator.Evaluate(0,true),Is.True,"Canonical toggle must invalidate the cached deformation");
            }
            finally { UnityEngine.Object.DestroyImmediate(shader); }
        }
        [TestCase("BouncingBalls")]
        [TestCase("Room")]
        public void ImportedCanonicalMatchesGpuActivation(string scene)
        {
            var asset=AssetDatabase.LoadAssetAtPath<GaussianSplat4DInferenceAsset>("Assets/Gaussians/4DGS/"+scene+"/Inference.asset");
            if(!asset || !asset.Canonical) Assert.Ignore("Requires imported development bundle.");
            var shader=Shader();
            try
            {
                using var evaluator=new GaussianSplat4DEvaluator(asset,shader);
                evaluator.Evaluate(0,false);
                var actual=new Vector4[asset.Data.GaussianCount*3];evaluator.Data.Splats.GetData(actual);
                var expected=asset.Canonical.floatSplats.GetData<Vector4>();
                for(int i=0;i<actual.Length;i++) for(int c=0;c<4;c++) Near(expected[i][c],actual[i][c]);
                var sh=new float[asset.Data.GaussianCount*evaluator.Data.CoefficientCount*3];evaluator.Data.SH.GetData(sh);
                CollectionAssert.AreEqual(asset.Canonical.floatSH.GetData<float>().ToArray(),sh);
            }
            finally { UnityEngine.Object.DestroyImmediate(shader); }
        }

        static void Near(float expected,float actual) => Assert.That(actual,Is.EqualTo(expected).Within(1e-5+1e-4*Math.Abs(expected)));

        [TestCase("BouncingBalls")]
        [TestCase("Room")]
        public void NativeAndOnnxAgreeAcrossEntireCloud(string scene)
        {
            var asset=AssetDatabase.LoadAssetAtPath<GaussianSplat4DInferenceAsset>("Assets/Gaussians/4DGS/"+scene+"/Inference.asset");
            if(!asset)Assert.Ignore("Requires development scene bundle.");
            var shader=Shader();
            try
            {
                var nativeShader=AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/net.kleinbeck.gaussians/Shaders/4DGS/NativeDeformation.compute");
                using var onnx=new GaussianSplat4DEvaluator(asset,shader);
                using var native=new GaussianSplat4DEvaluator(asset,shader,nativeShader);
                var a=new Vector4[asset.Data.GaussianCount*3];var b=new Vector4[a.Length];
                var shA=new float[asset.Data.GaussianCount*onnx.Data.CoefficientCount*3];var shB=new float[shA.Length];
                foreach(float time in new[]{0f,.371f,1f})
                {
                    onnx.Evaluate(time);native.Evaluate(time);
                    onnx.Data.Splats.GetData(a);native.Data.Splats.GetData(b);
                    onnx.Data.SH.GetData(shA);native.Data.SH.GetData(shB);
                    double worst=0;float smallestDot=1;
                    for(int i=0;i<a.Length;i++)
                    {
                        if(i%3==2){smallestDot=Mathf.Min(smallestDot,Mathf.Abs(Vector4.Dot(a[i],b[i])));continue;}
                        for(int k=0;k<4;k++)worst=Math.Max(worst,Math.Abs(a[i][k]-b[i][k])/(1e-5+1e-4*Math.Abs(a[i][k])));
                    }
                    for(int i=0;i<shA.Length;i++)worst=Math.Max(worst,Math.Abs(shA[i]-shB[i])/(1e-5+1e-4*Math.Abs(shA[i])));
                    Assert.That(worst,Is.LessThanOrEqualTo(1),"Full-cloud attribute tolerance at time "+time);
                    Assert.That(smallestDot,Is.GreaterThan(.99999f),"Full-cloud rotation parity at time "+time);
                }
            }
            finally{UnityEngine.Object.DestroyImmediate(shader);}
        }

    }
}
