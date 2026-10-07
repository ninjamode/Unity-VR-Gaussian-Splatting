// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Gaussians.Core.Editor.Utils;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;
namespace Gaussians.Core.Editor.Importing
{
    public struct GaussianImportCamera { public Vector3 pos, axisX, axisY, axisZ; public float fov; }
    public static class GaussianImportIO
    {
        const string kCamerasJson = "cameras.json";
        // Generic training filenames include parent folders so different models stay distinct.
        public static string SourceName(string path) => Path.GetFileNameWithoutExtension(FilePickerControl.PathToDisplayString(path));
        public static void ValidateOutputFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) throw new IOException("Select an output folder within Assets");
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if ((folder != "Assets" && !folder.StartsWith("Assets/", StringComparison.Ordinal)) || folder.Split('/').Contains(".."))
                throw new IOException("Output folder must be within Assets");
        }
        public static T CreateOrReplaceAsset<T>(T asset, string path) where T : ScriptableObject
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (!existing)
            {
                if (File.Exists(path)) throw new IOException("Output path contains an incompatible asset: " + path);
                AssetDatabase.CreateAsset(asset, path); return asset;
            }
            EditorUtility.CopySerialized(asset, existing);
            UnityEngine.Object.DestroyImmediate(asset);
            return existing;
        }
        public static void EmitSimpleDataFile<T>(NativeArray<T> data, string path, ref Hash128 hash) where T : unmanaged
        {
            hash.Append(data); Write(data.Reinterpret<byte>(UnsafeUtility.SizeOf<T>()), path);
        }
        public static void Write(NativeArray<byte> data, string path)
        { using var stream = File.Create(path); stream.Write(data); }
        public static int NextMultipleOf(int size, int multiple) => checked((size + multiple - 1) / multiple * multiple);
        public static GaussianImportCamera[] LoadCameras(string curPath, bool doImport)
        {
            if (!doImport)
                return null;

            string camerasPath;
            while (true)
            {
                var dir = Path.GetDirectoryName(curPath);
                if (!Directory.Exists(dir))
                    return null;
                camerasPath = $"{dir}/{kCamerasJson}";
                if (File.Exists(camerasPath))
                    break;
                curPath = dir;
            }

            if (!File.Exists(camerasPath))
                return null;

            string json = File.ReadAllText(camerasPath);
            var jsonCameras = JSONParser.FromJson<List<JsonCamera>>(json);
            if (jsonCameras == null || jsonCameras.Count == 0)
                return null;

            var result = new GaussianImportCamera[jsonCameras.Count];
            for (var camIndex = 0; camIndex < jsonCameras.Count; camIndex++)
            {
                var jsonCam = jsonCameras[camIndex];
                var pos = new Vector3(jsonCam.position[0], jsonCam.position[1], jsonCam.position[2]);
                // Read exported rotation columns, then flip Y/Z to Unity's camera convention.
                var axisx = new Vector3(jsonCam.rotation[0][0], jsonCam.rotation[1][0], jsonCam.rotation[2][0]);
                var axisy = new Vector3(jsonCam.rotation[0][1], jsonCam.rotation[1][1], jsonCam.rotation[2][1]);
                var axisz = new Vector3(jsonCam.rotation[0][2], jsonCam.rotation[1][2], jsonCam.rotation[2][2]);

                axisy *= -1;
                axisz *= -1;

                var cam = new GaussianImportCamera
                {
                    pos = pos,
                    axisX = axisx,
                    axisY = axisy,
                    axisZ = axisz,
                    fov = 25 // Camera previews use a fixed FOV; exported focal lengths are not applied.
                };
                result[camIndex] = cam;
            }

            return result;
        }

        [Serializable]
        public class JsonCamera
        {
            public int id;
            public string img_name;
            public int width;
            public int height;
            public float[] position;
            public float[][] rotation;
            public float fx;
            public float fy;
        }
    }
}
