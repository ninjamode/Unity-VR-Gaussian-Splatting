using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Gaussians.ThreeD;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Gaussians.FourD.Editor
{
    public static partial class GaussianSplat4DBundleImporter
    {
        public static GaussianSplat3DAsset CreateCanonical(GaussianSplat4DAsset data, string folder, GaussianSplatSHStorage storage = GaussianSplatSHStorage.Float32)
        {
            var json = JObject.Parse(data.Manifest.text);
            float[] Read(string name) => data.GetTensor((string)json["canonical"][name]).ReadFloats();
            float[] positions = Read("positions"), scales = Read("log_scales"), rotations = Read("rotations_wxyz"),
                opacity = Read("opacity_logits"), sh = Read("sh");
            var geometry = new float[checked(data.GaussianCount * 12)];
            Bounds bounds = default;
            bool hasBounds = false;
            for (int i = 0; i < data.GaussianCount; i++)
            {
                var position = new Vector3(positions[i*3], positions[i*3+1], positions[i*3+2]);
                var scale = new Vector3(Mathf.Exp(scales[i*3]), Mathf.Exp(scales[i*3+1]), Mathf.Exp(scales[i*3+2]));
                var q = new Vector4(rotations[i*4+1], rotations[i*4+2], rotations[i*4+3], rotations[i*4]);
                float norm = Vector4.Dot(q,q);
                float logit = opacity[i];
                float alpha = logit >= 0 ? 1 / (1 + Mathf.Exp(-logit)) : Mathf.Exp(logit) / (1 + Mathf.Exp(logit));
                if (!(norm > 1e-24f) || !float.IsFinite(norm) || !Finite(position) || !Finite(scale) || !float.IsFinite(alpha))
                { position = Vector3.zero; scale = Vector3.zero; alpha = 0; q = new Vector4(0,0,0,1); }
                else q *= 1 / Mathf.Sqrt(norm);
                int offset = i*12;
                geometry[offset] = position.x; geometry[offset+1] = position.y; geometry[offset+2] = position.z; geometry[offset+3] = alpha;
                geometry[offset+4] = scale.x; geometry[offset+5] = scale.y; geometry[offset+6] = scale.z;
                geometry[offset+8] = q.x; geometry[offset+9] = q.y; geometry[offset+10] = q.z; geometry[offset+11] = q.w;
                // Six sigma includes the renderer's supported static scale range (up to 2).
                float radius = 6 * Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z));
                var item = new Bounds(position, Vector3.one * radius * 2);
                if (!hasBounds) { bounds = item; hasBounds = true; }
                else { bounds.Encapsulate(item.min); bounds.Encapsulate(item.max); }
            }
            byte[] Bytes(float[] values) { var bytes = new byte[checked(values.Length*4)]; Buffer.BlockCopy(values,0,bytes,0,bytes.Length); return bytes; }
            var geometryBytes = Bytes(geometry);
            var shBytes = storage == GaussianSplatSHStorage.Float32 ? Bytes(sh) : PackSH(sh, data.GaussianCount, (data.SHDegree+1)*(data.SHDegree+1));
            string geometryPath = folder + "/canonical-render.bytes", shPath = folder + "/canonical-sh.bytes";
            File.WriteAllBytes(geometryPath, geometryBytes); File.WriteAllBytes(shPath, shBytes);
            AssetDatabase.ImportAsset(geometryPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(shPath, ImportAssetOptions.ForceSynchronousImport);
            // Bind raw canonical content, metadata and order, not just count or filename.
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes($"GaussianCanonical-v2:{data.GaussianCount}:{data.SHDegree}:{storage}:"));
            foreach (var values in new[]{positions, scales, rotations, opacity, sh}) hash.AppendData(Bytes(values));
            var identity = Hash128.Compute(BitConverter.ToString(hash.GetHashAndReset()));
            string assetPath = folder + "/Canonical3D.asset";
            var canonical = AssetDatabase.LoadAssetAtPath<GaussianSplat3DAsset>(assetPath);
            bool create = !canonical;
            if (create) canonical = ScriptableObject.CreateInstance<GaussianSplat3DAsset>();
            canonical.InitializeFloat(data.GaussianCount, data.SHDegree,
                AssetDatabase.LoadAssetAtPath<TextAsset>(geometryPath), AssetDatabase.LoadAssetAtPath<TextAsset>(shPath), bounds, identity, storage);
            if (create) AssetDatabase.CreateAsset(canonical, assetPath);
            else EditorUtility.SetDirty(canonical);
            return canonical;
        }
        public static byte[] PackSH(float[] values, int count, int coefficients)
        {
            int components = coefficients * 3, words = GaussianSplat3DData.SHWordsPerSplat(coefficients, GaussianSplatSHStorage.Float16);
            var result = new uint[checked(count * words)];
            for (int i = 0; i < count; i++) for (int k = 0; k < components; k++)
            {
                float value = values[i * components + k];
                if (!float.IsFinite(value) || Mathf.Abs(value) > 65504) throw new InvalidDataException("Canonical SH exceeds finite FP16 range; select Float32.");
                result[i * words + k / 2] |= (uint)Mathf.FloatToHalf(value) << ((k & 1) * 16);
            }
            var bytes = new byte[result.Length * 4]; Buffer.BlockCopy(result, 0, bytes, 0, bytes.Length); return bytes;
        }
        static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    }
}
