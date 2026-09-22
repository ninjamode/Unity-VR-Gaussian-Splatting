using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Gaussians.FourD.Inference;
using Gaussians.ThreeD;
using Newtonsoft.Json.Linq;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEngine;

namespace Gaussians.FourD.Editor
{
    public static partial class GaussianSplat4DBundleImporter
    {
        public static bool IsRuntimeTensor(JObject json, string name, Gaussian4DBackends backends)
        {
            if (((JObject)json["canonical"]).Properties().Any(p => (string)p.Value == name)) return true;
            if (backends == Gaussian4DBackends.Onnx) return false;
            if (name == "weights/deformation_net.grid.aabb" || name.StartsWith("weights/deformation_net.grid.grids.", StringComparison.Ordinal) ||
                name.StartsWith("weights/deformation_net.feature_out.", StringComparison.Ordinal)) return true;
            string[] heads = {"pos_deform", "scales_deform", "rotations_deform", "opacity_deform", "shs_deform"};
            string[] flags = {"no_dx", "no_ds", "no_dr", "no_do", "no_dshs"};
            for (int i=0;i<heads.Length;i++)
                if ((bool?)json["configuration"]?[flags[i]] != true && name.StartsWith("weights/deformation_net."+heads[i]+".",StringComparison.Ordinal)) return true;
            return false;
        }

        public static GaussianSplat4DInferenceAsset Import(string sourceDirectory, string destinationFolder,
            Gaussian4DBackends backends = Gaussian4DBackends.Both, GaussianSplatSHStorage canonicalSH = GaussianSplatSHStorage.Float32, bool mortonOrder = true)
        {
            destinationFolder = destinationFolder.Replace('\\','/').TrimEnd('/');
            string assetsRoot = Path.GetFullPath("Assets") + Path.DirectorySeparatorChar;
            if (!destinationFolder.StartsWith("Assets/",StringComparison.Ordinal) ||
                !Path.GetFullPath(destinationFolder).StartsWith(assetsRoot,StringComparison.Ordinal))
                throw new ArgumentException("Destination must be a folder under Assets/.");
            string ownerPath = destinationFolder + "/ImportSettings.asset";
            var previous = AssetDatabase.LoadAssetAtPath<Gaussian4DImportSettings>(ownerPath);
            bool existed = Directory.Exists(destinationFolder);
            if (existed && !previous) throw new IOException("Destination is not a managed 4D import. Choose a new model folder.");
            var json = CheckBundle(sourceDirectory, backends);
            string modelName = new DirectoryInfo(Path.GetFullPath(sourceDirectory)).Name;
            string canonicalFile = modelName + "-canonical3d.asset";
            string inferenceFile = modelName + "-inference.asset";
            string stage = "Assets/__GaussianImport_" + Guid.NewGuid().ToString("N");
            var backup = new Dictionary<string,byte[]>();
            var newFiles = new List<string>();
            bool publishing = false;
            try
            {
                Directory.CreateDirectory(stage);
                var canonicalNames = ((JObject)json["canonical"]).Properties().Select(p=>(string)p.Value).ToHashSet();
                int count = (int)json["gaussian_count"];
                int[] order = null;
                if(mortonOrder)
                {
                    byte[] bytes = File.ReadAllBytes(CheckedPath(sourceDirectory,(string)json["tensors"][(string)json["canonical"]["positions"]]["path"]));
                    var positions = new float[bytes.Length/4]; Buffer.BlockCopy(bytes,0,positions,0,bytes.Length);
                    order = Gaussian4DMortonOrder.Create(positions);
                }
                var entries = new List<GaussianTensorData>();
                using (var payload = File.Create(stage+"/deformation-data.bytes"))
                    foreach(var property in ((JObject)json["tensors"]).Properties())
                    {
                        if(!IsRuntimeTensor(json,property.Name,backends)) continue;
                        byte[] bytes = File.ReadAllBytes(CheckedPath(sourceDirectory,(string)property.Value["path"]));
                        if(order != null && canonicalNames.Contains(property.Name)) bytes=Gaussian4DMortonOrder.Apply(bytes,order,bytes.Length/count);
                        entries.Add(new GaussianTensorData { name=property.Name,dtype=(string)property.Value["dtype"],shape=property.Value["shape"].ToObject<int[]>(),packed=true,byteOffset=checked((int)payload.Position) });
                        payload.Write(bytes,0,bytes.Length);
                    }
                // Runtime configuration only: no fixture references or stale source hashes/paths.
                var runtimeJson = new JObject { ["configuration"]=json["configuration"]?.DeepClone(), ["canonical"]=json["canonical"].DeepClone() };
                File.WriteAllText(stage+"/runtime.json",runtimeJson.ToString());
                int simplified = 0;
                if(backends != Gaussian4DBackends.Native)
                    simplified=Gaussian4DOnnxCompatibility.Prepare(CheckedPath(sourceDirectory,(string)json["onnx"]["path"]),stage+"/deformation.onnx");
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                var model = AssetDatabase.LoadAssetAtPath<ModelAsset>(stage+"/deformation.onnx");
                if(backends != Gaussian4DBackends.Native && !model) throw new InvalidDataException("Unity could not import deformation.onnx.");
                var stagedData = ScriptableObject.CreateInstance<GaussianSplat4DAsset>();
                foreach(var entry in entries) entry.data=AssetDatabase.LoadAssetAtPath<TextAsset>(stage+"/deformation-data.bytes");
                stagedData.Initialize(count,(int)json["sh_degree"],(int)json["version"],AssetDatabase.LoadAssetAtPath<TextAsset>(stage+"/runtime.json"),entries.ToArray(),backends);
                AssetDatabase.CreateAsset(stagedData,stage+"/DeformationData.asset");
                var stagedCanonical = CreateCanonical(stagedData,stage,canonicalSH); // Includes FP16 range validation before publishing.
                var generated = new List<string> {"deformation-data.bytes","runtime.json","canonical-render.bytes","canonical-sh.bytes","DeformationData.asset",canonicalFile,inferenceFile,"ImportSettings.asset"};
                if(model) generated.Add("deformation.onnx");
                string[] oldFiles = previous ? previous.generatedFiles ?? Array.Empty<string>() : Array.Empty<string>();
                foreach(string file in oldFiles.Concat(generated).Distinct())
                {
                    if(Path.GetFileName(file)!=file) throw new InvalidDataException("Invalid generated-file ownership record.");
                    string path=destinationFolder+"/"+file;
                    // Never overwrite a user-created file absent from the previous ownership record.
                    if(existed && File.Exists(path) && !oldFiles.Contains(file)) throw new IOException("Unmanaged file blocks import: "+path);
                    foreach(string item in new[]{path,path+".meta"})
                        if(File.Exists(item)) backup[item]=File.ReadAllBytes(item); else newFiles.Add(item);
                }
                publishing = true;
                Directory.CreateDirectory(destinationFolder);
                foreach(string file in generated.Where(f=>!f.EndsWith(".asset",StringComparison.Ordinal))) File.Copy(stage+"/"+file,destinationFolder+"/"+file,true);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                var data = GetOrCreate<GaussianSplat4DAsset>(destinationFolder+"/DeformationData.asset");
                foreach(var entry in entries) entry.data=AssetDatabase.LoadAssetAtPath<TextAsset>(destinationFolder+"/deformation-data.bytes");
                data.Initialize(count,(int)json["sh_degree"],(int)json["version"],AssetDatabase.LoadAssetAtPath<TextAsset>(destinationFolder+"/runtime.json"),entries.ToArray(),backends);
                EditorUtility.SetDirty(data);
                var canonical = GetOrCreateOwned<GaussianSplat3DAsset>(destinationFolder,canonicalFile,oldFiles);
                canonical.InitializeFloat(count,data.SHDegree,AssetDatabase.LoadAssetAtPath<TextAsset>(destinationFolder+"/canonical-render.bytes"),AssetDatabase.LoadAssetAtPath<TextAsset>(destinationFolder+"/canonical-sh.bytes"),
                    new Bounds((stagedCanonical.boundsMin+stagedCanonical.boundsMax)*.5f,stagedCanonical.boundsMax-stagedCanonical.boundsMin),stagedCanonical.dataHash,canonicalSH);
                EditorUtility.SetDirty(canonical);
                var result = GetOrCreateOwned<GaussianSplat4DInferenceAsset>(destinationFolder,inferenceFile,oldFiles);
                result.Initialize(data,model?AssetDatabase.LoadAssetAtPath<ModelAsset>(destinationFolder+"/deformation.onnx"):null);
                result.SetCanonical(canonical);EditorUtility.SetDirty(result);
                var settings = GetOrCreate<Gaussian4DImportSettings>(ownerPath);
                settings.sourceDirectory=Path.GetFullPath(sourceDirectory);settings.backends=backends;settings.canonicalSH=canonicalSH;settings.mortonOrder=mortonOrder;
                settings.generatedFiles=generated.ToArray();settings.removedOnnxSqueezes=simplified;
                settings.runtimeTensorBytes=new FileInfo(destinationFolder+"/deformation-data.bytes").Length;
                settings.canonicalBytes=new FileInfo(destinationFolder+"/canonical-render.bytes").Length+new FileInfo(destinationFolder+"/canonical-sh.bytes").Length;
                settings.onnxBytes=model?new FileInfo(destinationFolder+"/deformation.onnx").Length:0;
                EditorUtility.SetDirty(settings);AssetDatabase.SaveAssets();
                foreach(string stale in oldFiles.Except(generated)) AssetDatabase.DeleteAsset(destinationFolder+"/"+stale);
                return result;
            }
            catch
            {
                if(publishing)
                {
                    if(!existed) AssetDatabase.DeleteAsset(destinationFolder);
                    else
                    {
                        foreach(string path in newFiles) if(File.Exists(path)) File.Delete(path);
                        foreach(var pair in backup) File.WriteAllBytes(pair.Key,pair.Value);
                        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    }
                }
                throw;
            }
            finally { AssetDatabase.DeleteAsset(stage); }
        }
        static T GetOrCreateOwned<T>(string folder, string file, string[] oldFiles) where T : ScriptableObject
        {
            string path = folder + "/" + file;
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (!asset)
            {
                // Follow the ownership record rather than a historical filename. This
                // preserves references when the source folder (and display name) changes.
                var candidates = oldFiles.Where(f => f.EndsWith(".asset", StringComparison.Ordinal))
                    .Select(f => folder + "/" + f).Where(p => AssetDatabase.LoadAssetAtPath<T>(p)).ToArray();
                if (candidates.Length > 1) throw new InvalidDataException("Ambiguous imported asset ownership: " + typeof(T).Name);
                if (candidates.Length == 1)
                {
                    string error = AssetDatabase.MoveAsset(candidates[0], path);
                    if (!string.IsNullOrEmpty(error)) throw new IOException(error);
                }
                asset = GetOrCreate<T>(path);
            }
            asset.name = Path.GetFileNameWithoutExtension(file);
            return asset;
        }
        static T GetOrCreate<T>(string path) where T : ScriptableObject
        {
            var asset=AssetDatabase.LoadAssetAtPath<T>(path);
            if(!asset) { asset=ScriptableObject.CreateInstance<T>();AssetDatabase.CreateAsset(asset,path); }
            return asset;
        }
    }
}
