using System;
using System.IO;
using System.Text;
using Gaussians.ThreeD;
using Gaussians.ThreeD.Editor.Utils;
using UnityEditor;
using UnityEngine;

namespace Gaussians.Package.Tests
{
    sealed class GeneratedGaussianCloud : IDisposable
    {
        public const int Count = 1000;
        public const int VisibleCount = 768;
        readonly string folder;
        public GaussianSplat3DAsset Asset { get; private set; }

        public static Vector3 Position(int index) => index < VisibleCount
            ? new Vector3((index % 32 - 15.5f) * .03f, (index / 32 - 11.5f) * .03f, 2 + index % 7 * .1f)
            : new Vector3(30 + index * .01f, 0, 2);

        public static void WritePly(string path, int count = Count)
        {
            using var writer = new BinaryWriter(File.Create(path));
            var header = new StringBuilder($"ply\nformat binary_little_endian 1.0\nelement vertex {count}\nproperty double confidence\n");
            foreach (string name in new[] { "z", "x", "y", "f_dc_0", "f_dc_1", "f_dc_2" })
                header.AppendLine("property float " + name);
            for (int i = 0; i < 45; i++) header.AppendLine("property float f_rest_" + i);
            foreach (string name in new[] { "opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3" })
                header.AppendLine("property float " + name);
            header.AppendLine("end_header");
            writer.Write(Encoding.ASCII.GetBytes(header.ToString()));
            for (int i = 0; i < count; i++)
            {
                var position = Position(i);
                writer.Write((double)i);
                writer.Write(position.z); writer.Write(position.x); writer.Write(position.y);
                writer.Write(0f); writer.Write(0f); writer.Write(0f);
                for (int j = 0; j < 45; j++) writer.Write((j + 1) * .001f);
                writer.Write(1.38629436f);
                writer.Write(Mathf.Log(.02f)); writer.Write(Mathf.Log(.02f)); writer.Write(Mathf.Log(.02f));
                writer.Write(1f); writer.Write(0f); writer.Write(0f); writer.Write(0f);
            }
        }

        public GeneratedGaussianCloud()
        {
            folder = AssetDatabase.GUIDToAssetPath(AssetDatabase.CreateFolder("Assets", "__GaussianBaseline_" + Guid.NewGuid().ToString("N")));
            if (string.IsNullOrEmpty(folder)) throw new IOException("Could not create the temporary Gaussian test folder.");
            try
            {
                string ply = folder + "/generated.ply";
                WritePly(ply);
                GaussianSplat3DFileReader.ReadFile(ply, out var input);
                using (input)
                {
                    using (var positions = new BinaryWriter(File.Create(folder + "/positions.bytes")))
                    using (var other = new BinaryWriter(File.Create(folder + "/other.bytes")))
                    using (var sh = new BinaryWriter(File.Create(folder + "/sh.bytes")))
                        foreach (var splat in input)
                        {
                            Write(positions, splat.pos);
                            var q = splat.rot;
                            uint rotation = (uint)Mathf.RoundToInt(q.x * 1023) | (uint)Mathf.RoundToInt(q.y * 1023) << 10 |
                                (uint)Mathf.RoundToInt(q.z * 1023) << 20 | (uint)Mathf.RoundToInt(q.w * 3) << 30;
                            other.Write(rotation); Write(other, splat.scale);
                            foreach (var coefficient in new[] { splat.sh1, splat.sh2, splat.sh3, splat.sh4, splat.sh5,
                                splat.sh6, splat.sh7, splat.sh8, splat.sh9, splat.shA, splat.shB, splat.shC, splat.shD, splat.shE, splat.shF })
                                Write(sh, coefficient);
                            Write(sh, Vector3.zero);
                        }

                    var (width, height) = GaussianSplat3DAsset.CalcTextureSize(Count);
                    using var colors = new BinaryWriter(File.Create(folder + "/colors.bytes"));
                    // Uniform color makes the fixture independent of the color texture's tile swizzle.
                    for (int i = 0; i < width * height; i++)
                    {
                        Write(colors, input[0].dc0); colors.Write(input[0].opacity);
                    }
                }
                TextAsset Load(string name)
                {
                    string path = folder + "/" + name + ".bytes";
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                    return AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                }
                Asset = ScriptableObject.CreateInstance<GaussianSplat3DAsset>();
                Asset.hideFlags = HideFlags.HideAndDontSave;
                Asset.Initialize(Count, GaussianSplat3DAsset.VectorFormat.Float32, GaussianSplat3DAsset.VectorFormat.Float32,
                    GaussianSplat3DAsset.ColorFormat.Float32x4, GaussianSplat3DAsset.SHFormat.Float32,
                    new Vector3(-1, -1, 2), new Vector3(40, 1, 3), null);
                Asset.SetAssetFiles(null, Load("positions"), Load("other"), Load("colors"), Load("sh"));
            }
            catch { Dispose(); throw; }
        }

        static void Write(BinaryWriter writer, Vector3 value)
        { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); }

        public void Dispose()
        {
            if (Asset) UnityEngine.Object.DestroyImmediate(Asset);
            if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            else if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
