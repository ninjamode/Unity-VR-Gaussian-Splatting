using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
        public static string CheckedPath(string root, string relative)
        {
            if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("Expected a relative bundle path.");
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException("Path escapes the bundle: " + relative);
            return path;
        }

        static void CheckHash(string path, string expected)
        {
            using var stream = File.OpenRead(path);
            using var hash = SHA256.Create();
            string actual = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Checksum mismatch: " + path);
        }

        public static JObject CheckBundle(string root, Gaussian4DBackends backends = Gaussian4DBackends.Both)
        {
            var json = JObject.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
            if ((string)json["format"] != "unity-gaussians.hexplane-4dgs" || (int)json["version"] is not (1 or 2))
                throw new InvalidDataException("Unsupported 4DGS bundle format/version.");
            if ((int)json["gaussian_count"] <= 0 || (int)json["sh_degree"] is < 0 or > 3)
                throw new InvalidDataException("Invalid Gaussian count or SH degree.");
            foreach (var property in ((JObject)json["tensors"]).Properties())
            {
                if (!IsRuntimeTensor(json, property.Name, backends)) continue;
                var tensor = property.Value;
                if ((string)tensor["dtype"] is not ("<f4" or "<i4")) throw new InvalidDataException("Unsupported dtype: " + property.Name);
                long count = 1;
                foreach (int size in tensor["shape"].ToObject<int[]>())
                {
                    if (size < 0) throw new InvalidDataException("Invalid tensor: " + property.Name);
                    count = checked(count * size);
                }
                string path = CheckedPath(root, (string)tensor["path"]);
                if (count > int.MaxValue / 4 || count * 4 != (long)tensor["byte_length"] || new FileInfo(path).Length != count * 4)
                    throw new InvalidDataException("Tensor byte length mismatch: " + property.Name);
                CheckHash(path, (string)tensor["sha256"]);
            }
            if (backends != Gaussian4DBackends.Native) CheckHash(CheckedPath(root, (string)json["onnx"]["path"]), (string)json["onnx"]["sha256"]);
            int n = (int)json["gaussian_count"], k = ((int)json["sh_degree"] + 1) * ((int)json["sh_degree"] + 1);
            int[][] shapes = { new[]{n,3}, new[]{n,3}, new[]{n,4}, new[]{n,1}, new[]{n,k,3} };
            for (int i = 0; i < shapes.Length; i++)
            {
                string name = (string)json["canonical"][GaussianSplat4DInference.Attributes[i]];
                var tensor = json["tensors"][name];
                if (tensor == null || (string)tensor["dtype"] != "<f4" || !shapes[i].SequenceEqual(tensor["shape"].ToObject<int[]>()))
                    throw new InvalidDataException("Invalid canonical tensor: " + name);
            }
            if (backends != Gaussian4DBackends.Onnx) CheckNativeStructure(json, root);
            return json;
        }

    }
}
