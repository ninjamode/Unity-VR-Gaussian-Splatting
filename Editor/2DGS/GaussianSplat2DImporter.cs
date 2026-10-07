// SPDX-License-Identifier: MIT
using Gaussians.Core.Editor.Importing;
using Gaussians.TwoD.Editor.Utils;
using UnityEditor;

namespace Gaussians.TwoD.Editor
{
    public static class GaussianSplat2DImporter
    {
        public static GaussianSplat2DAsset Import(string source, string folder, GaussianSplat2DImportSettings settings, bool importCameras = true)
        {
            GaussianImportIO.ValidateOutputFolder(folder);
            try
            {
                var cameras = GaussianSplat2DAssetBuilder.LoadJsonCamerasFile(source, importCameras);
                GaussianSplat2DFileReader.ReadFile(source, out var splats);
                using (splats) return new GaussianSplat2DAssetBuilder(settings).Build(splats, folder, GaussianImportIO.SourceName(source), cameras);
            }
            finally { EditorUtility.ClearProgressBar(); }
        }
    }
}
