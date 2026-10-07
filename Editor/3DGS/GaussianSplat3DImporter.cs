// SPDX-License-Identifier: MIT
using System;
using System.IO;
using Gaussians.Core.Editor.Importing;
using Gaussians.Core.Editor.Utils;
using Gaussians.ThreeD.Editor.Utils;
using UnityEditor;

namespace Gaussians.ThreeD.Editor
{
    public enum GaussianSplat3DSourceFormat { ThreeDGS, Layered3DGS }
    public readonly struct GaussianSplat3DSourceInfo
    {
        public readonly GaussianSplat3DSourceFormat Format;
        public readonly int SplatCount;
        public string DisplayName => Format == GaussianSplat3DSourceFormat.Layered3DGS ? "Layered 3DGS" : "3DGS";
        public GaussianSplat3DSourceInfo(GaussianSplat3DSourceFormat format, int count) { Format = format; SplatCount = count; }
    }

    public static class GaussianSplat3DImporter
    {
        public static GaussianSplat3DSourceInfo Inspect(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new IOException("Select an input PLY/SPZ file");
            if (path.EndsWith(".spz", StringComparison.OrdinalIgnoreCase))
            { SPZFileReader.ReadFileHeader(path, out int count); return new GaussianSplat3DSourceInfo(GaussianSplat3DSourceFormat.ThreeDGS, count); }
            if (!path.EndsWith(".ply", StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsupported Gaussian file format");
            var layout = PLYFileReader.ReadHeader(path);
            GaussianSplat3DFileReader.Validate(layout);
            bool layered = layout.HasProperty("layer");
            if (layered && layout.GetOffset("layer", PLYFileReader.ElementType.Int) < 0)
                throw new IOException("Layered 3DGS requires a scalar int/int32 layer property");
            return new GaussianSplat3DSourceInfo(layered ? GaussianSplat3DSourceFormat.Layered3DGS : GaussianSplat3DSourceFormat.ThreeDGS, layout.VertexCount);
        }

        public static GaussianSplat3DAsset[] Import(string source, string outputFolder, GaussianSplat3DImportSettings settings, bool importCameras = true)
        {
            GaussianImportIO.ValidateOutputFolder(outputFolder);
            try
            {
                var info = Inspect(source);
                if (info.SplatCount == 0) throw new IOException("The source contains no Gaussians to import");
                var cameras = GaussianSplat3DAssetBuilder.LoadJsonCamerasFile(source, importCameras);
                var builder = new GaussianSplat3DAssetBuilder(settings);
                string name = GaussianImportIO.SourceName(source);
                if (info.Format == GaussianSplat3DSourceFormat.Layered3DGS)
                    return LayeredGaussianSplat3DImporter.Import(source, outputFolder, name, builder, cameras);
                GaussianSplat3DFileReader.ReadFile(source, out var splats);
                using (splats) return new[] { builder.Build(splats, outputFolder, name, cameras) };
            }
            finally { EditorUtility.ClearProgressBar(); }
        }
    }
}
