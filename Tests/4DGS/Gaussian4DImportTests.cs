using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Gaussians.FourD.Editor;
using Gaussians.FourD.Inference;
using Gaussians.ThreeD;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Gaussians.FourD.Tests
{
    public sealed class Gaussian4DImportTests
    {
        string m_Source, m_Destination;
        [SetUp] public void Setup()
        {
            m_Source=Path.Combine(Path.GetTempPath(),"gaussian-source-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(m_Source);
            m_Destination="Assets/__GaussianImportTest_"+Guid.NewGuid().ToString("N");
        }
        [TearDown] public void Cleanup() { AssetDatabase.DeleteAsset(m_Destination);Directory.Delete(m_Source,true); }
        void Export(GaussianSplat4DAsset data)
        {
            var json=JObject.Parse(data.Manifest.text);
            json["format"]="unity-gaussians.hexplane-4dgs";json["version"]=2;json["gaussian_count"]=data.GaussianCount;json["sh_degree"]=data.SHDegree;
            var tensors=new JObject();int index=0;
            foreach(var tensor in data.Tensors)
            {
                byte[] bytes=tensor.data.bytes;string file=(index++)+".bin";File.WriteAllBytes(Path.Combine(m_Source,file),bytes);
                using var hash=SHA256.Create();
                tensors[tensor.name]=new JObject { ["path"]=file,["dtype"]=tensor.dtype,["shape"]=new JArray(tensor.shape),["byte_length"]=bytes.Length,["sha256"]=BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","").ToLowerInvariant() };
            }
            // Deliberately unavailable fixtures/ONNX: Native imports must not depend on them.
            tensors["fixtures/not-shipped"]=new JObject{["path"]="missing.bin"};json["tensors"]=tensors;
            File.WriteAllText(Path.Combine(m_Source,"manifest.json"),json.ToString());
        }
        [Test] public void MortonOrderIsStableAndAppliesWholeAttributeRows()
        {
            var order=Gaussian4DMortonOrder.Create(new float[]{1,1,1, 0,0,0, 1,1,1, 0,1,0});
            CollectionAssert.AreEqual(new[]{1,3,0,2},order);
            byte[] rows={10,11,20,21,30,31,40,41};
            CollectionAssert.AreEqual(new byte[]{20,21,40,41,10,11,30,31},Gaussian4DMortonOrder.Apply(rows,order,2));
            Assert.Throws<InvalidDataException>(()=>Gaussian4DMortonOrder.Create(new[]{float.NaN,0,0}));
        }
        [Test] public void RuntimeImportReordersBothPathsAndReimportPreservesReferences()
        {
            using var fixture=new Gaussian4DNativeTests.SyntheticBundle(31,false,false);Export(fixture.Asset);
            var imported=GaussianSplat4DBundleImporter.Import(m_Source,m_Destination,Gaussian4DBackends.Native,GaussianSplatSHStorage.Float16);
            Assert.That(imported.Model,Is.Null);Assert.That(imported.Data.Backends,Is.EqualTo(Gaussian4DBackends.Native));
            Assert.That(imported.Data.Tensors.All(t=>!t.name.StartsWith("fixtures/")),Is.True);
            Assert.That(imported.Data.Tensors.Select(t=>t.data).Distinct().Count(),Is.EqualTo(1));
            Assert.That(Directory.Exists(m_Destination+"/Tensors"),Is.False);
            var order=Gaussian4DMortonOrder.Create(fixture.Asset.GetTensor("canonical/positions").ReadFloats());
            foreach(string name in new[]{"positions","log_scales","rotations_wxyz","opacity_logits","sh"})
            {
                var original=fixture.Asset.GetTensor("canonical/"+name).ReadFloats();var actual=imported.Data.GetTensor("canonical/"+name).ReadFloats();int width=original.Length/order.Length;
                for(int i=0;i<order.Length;i++) for(int k=0;k<width;k++) Assert.That(actual[i*width+k],Is.EqualTo(original[order[i]*width+k]));
            }
            var native=AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/net.kleinbeck.gaussians/Shaders/4DGS/NativeDeformation.compute");
            var activation=AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/net.kleinbeck.gaussians/Shaders/4DGS/GaussianSplat4D.compute");
            using(var original=new GaussianSplat4DNative(fixture.Asset,native,activation))
            using(var reordered=new GaussianSplat4DNative(imported.Data,native,activation))
            {
                var a=new Vector4[order.Length*3];var b=new Vector4[a.Length];
                foreach(float time in new[]{0f,.37f,1f})
                {
                    original.Evaluate(time,true);reordered.Evaluate(time,true);original.Data.Splats.GetData(a);reordered.Data.Splats.GetData(b);
                    for(int i=0;i<order.Length;i++)for(int k=0;k<3;k++) Assert.That(Vector4.Distance(a[order[i]*3+k],b[i*3+k]),Is.LessThan(1e-5));
                }
            }
            string canonicalPath=AssetDatabase.GetAssetPath(imported.Canonical),inferencePath=AssetDatabase.GetAssetPath(imported);
            string modelName=new DirectoryInfo(m_Source).Name;
            Assert.That(imported.name,Is.EqualTo(modelName+"-inference"));
            Assert.That(imported.Canonical.name,Is.EqualTo(modelName+"-canonical3d"));
            Assert.That(Path.GetFileName(inferencePath),Is.EqualTo(imported.name+".asset"));
            Assert.That(Path.GetFileName(canonicalPath),Is.EqualTo(imported.Canonical.name+".asset"));
            string canonicalGuid=AssetDatabase.AssetPathToGUID(canonicalPath),inferenceGuid=AssetDatabase.AssetPathToGUID(inferencePath);
            Assert.That(canonicalGuid,Is.Not.Empty);Assert.That(inferenceGuid,Is.Not.Empty);
            File.WriteAllText(m_Destination+"/user.txt","keep");
            var reimported=GaussianSplat4DBundleImporter.Import(m_Source,m_Destination,Gaussian4DBackends.Native,GaussianSplatSHStorage.Float32,false);
            Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(reimported.Canonical)),Is.EqualTo(canonicalGuid));
            Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(reimported)),Is.EqualTo(inferenceGuid));
            Assert.That(reimported,Is.EqualTo(imported));Assert.That(reimported.Matches(reimported.Canonical),Is.True);
            Assert.That(File.ReadAllText(m_Destination+"/user.txt"),Is.EqualTo("keep"));
            CollectionAssert.AreEqual(fixture.Asset.GetTensor("canonical/positions").ReadFloats(),reimported.Data.GetTensor("canonical/positions").ReadFloats());
            byte[] before=File.ReadAllBytes(m_Destination+"/deformation-data.bytes");
            File.WriteAllText(Path.Combine(m_Source,"0.bin"),"corrupt");
            Assert.Throws<InvalidDataException>(()=>GaussianSplat4DBundleImporter.Import(m_Source,m_Destination,Gaussian4DBackends.Native));
            CollectionAssert.AreEqual(before,File.ReadAllBytes(m_Destination+"/deformation-data.bytes"));
        }
        [Test] public void ReimportRenamesOwnedAssetsWithoutChangingTheirIdentity()
        {
            using var fixture=new Gaussian4DNativeTests.SyntheticBundle(31,false,false);Export(fixture.Asset);
            var asset=GaussianSplat4DBundleImporter.Import(m_Source,m_Destination,Gaussian4DBackends.Native);
            var canonical=asset.Canonical;
            string originalInference=AssetDatabase.GetAssetPath(asset),originalCanonical=AssetDatabase.GetAssetPath(canonical);
            string inferenceGuid=AssetDatabase.AssetPathToGUID(originalInference),canonicalGuid=AssetDatabase.AssetPathToGUID(originalCanonical);
            // An existing managed import can have generic names from an earlier import.
            Assert.That(AssetDatabase.MoveAsset(originalInference,m_Destination+"/Inference.asset"),Is.Empty);
            Assert.That(AssetDatabase.MoveAsset(originalCanonical,m_Destination+"/Canonical3D.asset"),Is.Empty);
            var settings=AssetDatabase.LoadAssetAtPath<Gaussian4DImportSettings>(m_Destination+"/ImportSettings.asset");
            settings.generatedFiles=settings.generatedFiles.Select(f=>f==Path.GetFileName(originalInference)?"Inference.asset":f==Path.GetFileName(originalCanonical)?"Canonical3D.asset":f).ToArray();
            EditorUtility.SetDirty(settings);AssetDatabase.SaveAssets();
            string renamed=m_Source+"-renamed";Directory.Move(m_Source,renamed);m_Source=renamed;
            var reimported=GaussianSplat4DBundleImporter.Import(m_Source,m_Destination,Gaussian4DBackends.Native);
            string prefix=new DirectoryInfo(m_Source).Name;
            Assert.That(reimported,Is.EqualTo(asset));Assert.That(reimported.Canonical,Is.EqualTo(canonical));
            Assert.That(reimported.name,Is.EqualTo(prefix+"-inference"));Assert.That(canonical.name,Is.EqualTo(prefix+"-canonical3d"));
            Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset)),Is.EqualTo(inferenceGuid));
            Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(canonical)),Is.EqualTo(canonicalGuid));
            Assert.That(File.Exists(m_Destination+"/Inference.asset"),Is.False);
            Assert.That(File.Exists(m_Destination+"/Canonical3D.asset"),Is.False);
            Assert.That(settings.generatedFiles,Does.Contain(prefix+"-inference.asset"));
        }

        [Test] public void InvalidNativeReimportDoesNotReplaceWorkingAssets()
        {
            using var fixture=new Gaussian4DNativeTests.SyntheticBundle(31,false,false);Export(fixture.Asset);
            var asset=GaussianSplat4DBundleImporter.Import(m_Source,m_Destination,Gaussian4DBackends.Native);
            int revision=asset.Revision;byte[] before=File.ReadAllBytes(m_Destination+"/deformation-data.bytes");
            var manifest=JObject.Parse(File.ReadAllText(Path.Combine(m_Source,"manifest.json")));
            ((JObject)manifest["tensors"]).Remove("weights/deformation_net.pos_deform.1.weight");
            File.WriteAllText(Path.Combine(m_Source,"manifest.json"),manifest.ToString());
            Assert.Throws<InvalidDataException>(()=>GaussianSplat4DBundleImporter.Import(m_Source,m_Destination,Gaussian4DBackends.Native));
            Assert.That(asset.Revision,Is.EqualTo(revision));CollectionAssert.AreEqual(before,File.ReadAllBytes(m_Destination+"/deformation-data.bytes"));
            Export(fixture.Asset);manifest=JObject.Parse(File.ReadAllText(Path.Combine(m_Source,"manifest.json")));manifest["configuration"]["grid_pe"]=1;
            File.WriteAllText(Path.Combine(m_Source,"manifest.json"),manifest.ToString());
            Assert.Throws<InvalidDataException>(()=>GaussianSplat4DBundleImporter.Import(m_Source,m_Destination,Gaussian4DBackends.Native));
        }
        [Test] public void BackendRemovalReleasesLiveGpuBuffersOnNextPreparation()
        {
            using var fixture=new Gaussian4DNativeTests.SyntheticBundle(31,false,false);Export(fixture.Asset);
            var asset=GaussianSplat4DBundleImporter.Import(m_Source,m_Destination,Gaussian4DBackends.Native);
            var go=new GameObject("Backend removal test");go.SetActive(false);
            try
            {
                var renderer=go.AddComponent<GaussianSplat3DRenderer>();renderer.m_Asset=asset.Canonical;
                var addon=go.AddComponent<GaussianSplat4D>();addon.m_Asset=asset;addon.m_Backend=GaussianSplat4D.DeformationBackend.Native;go.SetActive(true);
                Assert.That(addon.TryGetFrame(asset.Canonical,out var frame),Is.True);
                var data=asset.Data;data.Initialize(data.GaussianCount,data.SHDegree,data.BundleVersion,data.Manifest,data.Tensors.ToArray(),Gaussian4DBackends.Onnx);
                asset.Initialize(data,null);
                Assert.That(addon.TryGetFrame(asset.Canonical,out _),Is.False);
                Assert.That(addon.Error,Does.Contain("Native backend was not included"));Assert.That(frame.Data.Splats,Is.Null);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
        [Test] public void ExistingUnmanagedFolderIsNeverOverwritten()
        {
            Directory.CreateDirectory(m_Destination);File.WriteAllText(m_Destination+"/user.txt","keep");
            Assert.Throws<IOException>(()=>GaussianSplat4DBundleImporter.Import(m_Source,m_Destination));
            Assert.That(File.ReadAllText(m_Destination+"/user.txt"),Is.EqualTo("keep"));
        }
        [TestCase(1)] [TestCase(4)] [TestCase(9)] [TestCase(16)]
        public void CpuAndGpuPackedSHAgreeIncludingOddDegreeZeroRows(int coefficients)
        {
            const int count=3;
            var shader=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/net.kleinbeck.gaussians/Shaders/4DGS/GaussianSplat4D.compute"));
            var buffers=new System.Collections.Generic.Dictionary<string,ComputeBuffer>();
            try
            {
                void Add(string name,float[] values) { var buffer=new ComputeBuffer(values.Length,4);buffer.SetData(values);buffers.Add(name,buffer); }
                Add("positions",new float[count*3]);Add("log_scales",new float[count*3]);Add("opacity_logits",new float[count]);
                Add("rotations_wxyz",new float[]{1,0,0,0,1,0,0,0,1,0,0,0});
                var values=Enumerable.Range(0,count*coefficients*3).Select(i=>Mathf.Sin(i*.13f)*2).ToArray();Add("sh",values);
                using var data=new GaussianSplat4DGpuData(count,coefficients,shader,GaussianSplatSHStorage.Float16);data.Apply(buffers);
                var actual=new uint[data.SH.count];data.SH.GetData(actual);var expected=GaussianSplat4DBundleImporter.PackSH(values,count,coefficients);
                byte[] actualBytes=new byte[actual.Length*4];Buffer.BlockCopy(actual,0,actualBytes,0,actualBytes.Length);CollectionAssert.AreEqual(expected,actualBytes);
            }
            finally { foreach(var buffer in buffers.Values)buffer.Dispose();UnityEngine.Object.DestroyImmediate(shader); }
        }
    }
}
