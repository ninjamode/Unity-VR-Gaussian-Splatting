// SPDX-License-Identifier: MIT

using System.IO;
using Gaussians.Core.Editor.Utils;
using Gaussians.Core.Editor.Importing;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Gaussians.ThreeD.Editor.Utils
{
    // input file splat data is read into this format
    public struct InputSplatData
    {
        public Vector3 pos;
        public Vector3 nor;
        public Vector3 dc0;
        public Vector3 sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, shA, shB, shC, shD, shE, shF;
        public float opacity;
        public Vector3 scale;
        public Quaternion rot;
    }

    [BurstCompile]
    public class GaussianSplat3DFileReader
    {
        static readonly string[] Fields = {
                "x",
                "y",
                "z",
                "nx",
                "ny",
                "nz",
                "f_dc_0",
                "f_dc_1",
                "f_dc_2",
                "f_rest_0",
                "f_rest_1",
                "f_rest_2",
                "f_rest_3",
                "f_rest_4",
                "f_rest_5",
                "f_rest_6",
                "f_rest_7",
                "f_rest_8",
                "f_rest_9",
                "f_rest_10",
                "f_rest_11",
                "f_rest_12",
                "f_rest_13",
                "f_rest_14",
                "f_rest_15",
                "f_rest_16",
                "f_rest_17",
                "f_rest_18",
                "f_rest_19",
                "f_rest_20",
                "f_rest_21",
                "f_rest_22",
                "f_rest_23",
                "f_rest_24",
                "f_rest_25",
                "f_rest_26",
                "f_rest_27",
                "f_rest_28",
                "f_rest_29",
                "f_rest_30",
                "f_rest_31",
                "f_rest_32",
                "f_rest_33",
                "f_rest_34",
                "f_rest_35",
                "f_rest_36",
                "f_rest_37",
                "f_rest_38",
                "f_rest_39",
                "f_rest_40",
                "f_rest_41",
                "f_rest_42",
                "f_rest_43",
                "f_rest_44",
                "opacity",
                "scale_0",
                "scale_1",
                "scale_2",
                "rot_0",
                "rot_1",
                "rot_2",
                "rot_3",
            };
        internal static void Validate(PLYVertexLayout layout) => layout.RequireFloatProperties(new[] {
            "x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2", "opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3" });
        public static int ReadFileHeader(string path) => File.Exists(path) ? GaussianSplat3DImporter.Inspect(path).SplatCount : 0;
        public static void ReadFile(string path, out NativeArray<InputSplatData> splats)
        {
            splats = default;
            if (path.EndsWith(".spz", System.StringComparison.OrdinalIgnoreCase)) { SPZFileReader.ReadFile(path, out splats); return; }
            if (!path.EndsWith(".ply", System.StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsupported Gaussian source: " + path);
            PLYFileReader.ReadFile(path, out var layout, out var raw);
            using (raw) splats = DecodePLY(raw, layout);
        }
        internal static NativeArray<InputSplatData> DecodePLY(NativeArray<byte> raw, PLYVertexLayout layout)
        {
            Validate(layout);
            var splats = GaussianPlyMapping.Decode<InputSplatData>(raw, layout, Fields);
            try { GaussianPlyMapping.ReorderSH(splats, 9 * sizeof(float)); LinearizeData(splats); return splats; }
            catch { splats.Dispose(); throw; }
        }
        [BurstCompile]
        struct LinearizeDataJob : IJobParallelFor
        {
            public NativeArray<InputSplatData> splatData;
            public void Execute(int index)
            {
                var splat = splatData[index];

                // rot
                var q = splat.rot;
                var qq = GaussianUtils.NormalizeSwizzleRotation(new float4(q.x, q.y, q.z, q.w));
                qq = GaussianUtils.PackSmallest3Rotation(qq);
                splat.rot = new Quaternion(qq.x, qq.y, qq.z, qq.w);

                // scale
                splat.scale = GaussianUtils.LinearScale(splat.scale);

                // color
                splat.dc0 = GaussianUtils.SH0ToColor(splat.dc0);
                splat.opacity = GaussianUtils.Sigmoid(splat.opacity);

                splatData[index] = splat;
            }
        }

        static void LinearizeData(NativeArray<InputSplatData> splatData)
        {
            LinearizeDataJob job = new LinearizeDataJob();
            job.splatData = splatData;
            job.Schedule(splatData.Length, 4096).Complete();
        }
    }
}
