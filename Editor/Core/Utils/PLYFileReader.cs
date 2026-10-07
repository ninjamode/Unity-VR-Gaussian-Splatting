// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Gaussians.Core.Editor.Utils
{
    public sealed class PLYVertexLayout
    {
        public int VertexCount { get; }
        public int VertexStride { get; }
        public long DataOffset { get; }
        public IReadOnlyList<(string, PLYFileReader.ElementType)> Attributes { get; }
        readonly Dictionary<string, (PLYFileReader.ElementType type, int offset)> properties = new(StringComparer.Ordinal);
        internal PLYVertexLayout(int count, long offset, List<(string, PLYFileReader.ElementType)> attributes)
        {
            VertexCount = count; DataOffset = offset; Attributes = attributes.AsReadOnly();
            int stride = 0;
            foreach (var attribute in attributes)
            {
                if (properties.ContainsKey(attribute.Item1)) throw new IOException($"PLY has duplicate vertex property '{attribute.Item1}'");
                properties.Add(attribute.Item1, (attribute.Item2, stride));
                stride = checked(stride + PLYFileReader.TypeToSize(attribute.Item2));
            }
            VertexStride = stride;
            if ((long)count * stride > int.MaxValue) throw new IOException("PLY vertex payload exceeds the supported 2GB buffer size");
        }
        public bool HasProperty(string name) => properties.ContainsKey(name);
        public int GetOffset(string name, PLYFileReader.ElementType type) => properties.TryGetValue(name, out var value) && value.type == type ? value.offset : -1;
        public void RequireFloatProperties(IEnumerable<string> names)
        {
            var missing = names.Where(name => GetOffset(name, PLYFileReader.ElementType.Float) < 0).ToArray();
            if (missing.Length != 0) throw new IOException("Gaussian PLY is missing float properties: " + string.Join(", ", missing));
        }
    }
    public static class PLYFileReader
    {
        public static PLYVertexLayout ReadHeader(string path) { using var stream = File.OpenRead(path); return ReadHeader(path, stream); }
        public static void ReadFileHeader(string path, out int count, out int stride, out List<(string, ElementType)> attributes)
        {
            count = stride = 0; attributes = new();
            if (!File.Exists(path)) return;
            var layout = ReadHeader(path); count = layout.VertexCount; stride = layout.VertexStride; attributes = layout.Attributes.ToList();
        }
        static PLYVertexLayout ReadHeader(string path, FileStream stream)
        {
            if (stream.Length >= 2 * 1024 * 1024 * 1024L) throw new IOException($"PLY {path}: files larger than 2GB are not supported");
            if (ReadLine(stream) != "ply") throw new IOException($"File {path} has no PLY signature");
            int count = -1; bool binary = false, vertex = false, ended = false;
            var attributes = new List<(string, ElementType)>();
            for (int i = 0; i < 9000; ++i)
            {
                string line = ReadLine(stream);
                if (line == "end_header") { ended = true; break; }
                if (stream.Position == stream.Length && line.Length == 0) break;
                var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0 || tokens[0] == "comment" || tokens[0] == "obj_info") continue;
                if (tokens[0] == "format")
                {
                    if (tokens.Length != 3 || tokens[1] != "binary_little_endian" || tokens[2] != "1.0" || binary) throw new IOException($"PLY {path}: needs binary_little_endian 1.0 format");
                    binary = true;
                }
                else if (tokens[0] == "element")
                {
                    if (tokens.Length != 3 || !int.TryParse(tokens[2], out int n) || n < 0) throw new IOException($"PLY {path}: invalid element declaration");
                    vertex = tokens[1] == "vertex";
                    if (vertex) { if (count >= 0) throw new IOException($"PLY {path}: duplicate vertex element"); count = n; }
                    else if (count < 0 && n != 0) throw new IOException($"PLY {path}: vertex data must be the first nonempty element");
                }
                else if (tokens[0] == "property" && vertex)
                {
                    if (tokens.Length != 3) throw new IOException($"PLY {path}: expected a scalar vertex property, got '{line}'");
                    var type = tokens[1] switch
                    {
                        "float" => ElementType.Float, "double" => ElementType.Double, "uchar" => ElementType.UChar,
                        "int" or "int32" => ElementType.Int,
                        _ => throw new IOException($"PLY {path}: unsupported property type '{tokens[1]}'")
                    };
                    attributes.Add((tokens[2], type));
                }
            }
            if (!binary || !ended || count < 0 || attributes.Count == 0) throw new IOException($"PLY {path}: incomplete binary vertex header");
            return new PLYVertexLayout(count, stream.Position, attributes);
        }
        public static unsafe void ReadFile(string path, out PLYVertexLayout layout, out NativeArray<byte> vertices)
        {
            vertices = default; using var stream = File.OpenRead(path); layout = ReadHeader(path, stream);
            int length = checked(layout.VertexCount * layout.VertexStride);
            if (stream.Length - layout.DataOffset < length) throw new IOException($"PLY {path}: truncated vertex data, expected {length} bytes, got {stream.Length - layout.DataOffset}");
            vertices = new NativeArray<byte>(length, Allocator.Persistent);
            try
            {
                int offset = 0; byte* data = (byte*)vertices.GetUnsafePtr();
                while (offset < length)
                {
                    int read = stream.Read(new Span<byte>(data + offset, length - offset));
                    if (read == 0) throw new IOException($"PLY {path}: truncated vertex data");
                    offset += read;
                }
            }
            catch { vertices.Dispose(); vertices = default; throw; }
        }
        public static void ReadFile(string path, out int count, out int stride, out List<(string, ElementType)> attributes, out NativeArray<byte> vertices)
        {
            ReadFile(path, out var layout, out vertices); count = layout.VertexCount; stride = layout.VertexStride; attributes = layout.Attributes.ToList();
        }
        public enum ElementType { None, Float, Double, UChar, Int }
        public static int TypeToSize(ElementType type) => type switch { ElementType.Float or ElementType.Int => 4, ElementType.Double => 8, ElementType.UChar => 1, _ => throw new ArgumentOutOfRangeException(nameof(type)) };
        static string ReadLine(FileStream stream)
        {
            var bytes = new List<byte>();
            for (int b = stream.ReadByte(); b >= 0 && b != '\n'; b = stream.ReadByte())
            { bytes.Add((byte)b); if (bytes.Count > 65536) throw new IOException("PLY header line exceeds 64KB"); }
            if (bytes.Count > 0 && bytes[bytes.Count - 1] == '\r') bytes.RemoveAt(bytes.Count - 1);
            return Encoding.UTF8.GetString(bytes.ToArray());
        }
    }
}
