// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Gaussians.Core.Editor.Utils;
using Gaussians.ThreeD.Editor.Utils;
using Unity.Collections;

namespace Gaussians.ThreeD.Editor
{
    public readonly struct GaussianSplat3DLayer
    {
        public readonly int Id;
        public readonly NativeArray<InputSplatData> Splats;
        public GaussianSplat3DLayer(int id, NativeArray<InputSplatData> splats) { Id = id; Splats = splats; }
    }
    public sealed class LayeredGaussianSplat3DData : IDisposable
    {
        public IReadOnlyList<GaussianSplat3DLayer> Layers { get; }
        bool disposed;
        internal LayeredGaussianSplat3DData(List<GaussianSplat3DLayer> layers) => Layers = layers.AsReadOnly();
        public void Dispose()
        {
            if (disposed) return;
            foreach (var layer in Layers) if (layer.Splats.IsCreated) layer.Splats.Dispose();
            disposed = true;
        }
    }

    public static class LayeredGaussianSplat3DImporter
    {
        public static LayeredGaussianSplat3DData ReadLayers(string source)
        {
            PLYFileReader.ReadFile(source, out var layout, out var raw);
            using (raw)
            {
                GaussianSplat3DFileReader.Validate(layout);
                int offset = layout.GetOffset("layer", PLYFileReader.ElementType.Int);
                if (offset < 0) throw new IOException("Layered 3DGS requires a scalar int/int32 layer property");
                var labels = new int[layout.VertexCount];
                var counts = new SortedDictionary<int, int>();
                for (int i = 0; i < labels.Length; ++i)
                {
                    int address = i * layout.VertexStride + offset;
                    int id = raw[address] | raw[address + 1] << 8 | raw[address + 2] << 16 | raw[address + 3] << 24;
                    labels[i] = id; counts.TryGetValue(id, out int count); counts[id] = count + 1;
                }
                using var decoded = GaussianSplat3DFileReader.DecodePLY(raw, layout);
                var layers = new List<GaussianSplat3DLayer>();
                try
                {
                    var indices = new Dictionary<int, int>();
                    var cursors = new int[counts.Count];
                    foreach (var pair in counts)
                    {
                        if (pair.Value > GaussianSplat3DAsset.kMaxSplats) throw new IOException("Layer exceeds supported Gaussian count: " + pair.Key);
                        indices.Add(pair.Key, layers.Count);
                        layers.Add(new GaussianSplat3DLayer(pair.Key, new NativeArray<InputSplatData>(pair.Value, Allocator.Persistent)));
                    }
                    for (int row = 0; row < labels.Length; ++row)
                    { int index = indices[labels[row]]; var splats = layers[index].Splats; splats[cursors[index]++] = decoded[row]; }
                    return new LayeredGaussianSplat3DData(layers);
                }
                catch { foreach (var layer in layers) layer.Splats.Dispose(); throw; }
            }
        }
        internal static GaussianSplat3DAsset[] Import(string source, string folder, string name, GaussianSplat3DAssetBuilder builder, GaussianSplat3DAsset.CameraInfo[] cameras)
        {
            using var data = ReadLayers(source);
            return data.Layers.Select(layer => builder.Build(layer.Splats, folder, name + "_layer_" + layer.Id, cameras)).ToArray();
        }
    }
}
