using System;
using System.Collections;
using System.IO;
using System.Linq;
using Google.Protobuf;

namespace Gaussians.FourD.Editor
{
    /// <summary>Remove redundant rank-changing squeezes before fixed-shape reshapes.
    /// Unity 2.6 import analytics cannot handle their dynamic intermediate rank.</summary>
    public static class Gaussian4DOnnxCompatibility
    {
        static object Get(IMessage message, string field) => message.Descriptor.FindFieldByName(field).Accessor.GetValue(message);
        static void Set(IMessage message, string field, object value) => message.Descriptor.FindFieldByName(field).Accessor.SetValue(message, value);
        static IList List(IMessage message, string field) => (IList)Get(message, field);
        static string Text(IMessage message, string field) => (string)Get(message, field);

        public static int Prepare(string source, string destination)
        {
            // The package's generated ONNX messages are internal. Use protobuf's public
            // descriptor API after locating its parser, instead of vendoring a second schema.
            var assembly = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Unity.InferenceEngine.Editor");
            var type = assembly.GetType("Unity.InferenceEngine.Editor.Onnx.ModelProto", true);
            var parser = (MessageParser)type.GetProperty("Parser").GetValue(null);
            var model = parser.ParseFrom(File.ReadAllBytes(source));
            var graph = (IMessage)Get(model, "graph");
            var nodes = List(graph, "node").Cast<IMessage>().ToArray();
            var graphOutputs = List(graph, "output").Cast<IMessage>().Select(m => Text(m, "name")).ToHashSet();
            int changed = 0;
            foreach (var node in nodes)
            {
                if (Text(node, "op_type") != "Squeeze" || List(node, "input").Count != 1 || List(node, "output").Count != 1) continue;
                if (List(node, "attribute").Cast<IMessage>().Any(a => Text(a, "name") == "axes")) continue;
                string output = (string)List(node, "output")[0];
                if (graphOutputs.Contains(output)) continue;
                var consumers = nodes.Where(n => List(n, "input").Cast<string>().Contains(output)).ToArray();
                if (consumers.Length != 1) continue;
                var reshape = consumers[0];
                if (Text(reshape, "op_type") != "Reshape" || List(reshape, "input").Count != 2 || (string)List(reshape, "input")[0] != output) continue;
                string shapeName = (string)List(reshape, "input")[1];
                var constant = nodes.FirstOrDefault(n => Text(n, "op_type") == "Constant" && List(n, "output").Cast<string>().Contains(shapeName));
                if (constant == null) continue;
                var attribute = List(constant, "attribute").Cast<IMessage>().FirstOrDefault(a => Text(a, "name") == "value");
                if (attribute == null) continue;
                var tensor = (IMessage)Get(attribute, "t");
                if ((int)Get(tensor, "data_type") != 7) continue; // INT64 shape tensor
                long[] shape = List(tensor, "int64_data").Cast<long>().ToArray();
                var raw = ((ByteString)Get(tensor, "raw_data")).ToByteArray();
                if (raw.Length > 0)
                {
                    if (raw.Length % 8 != 0) throw new InvalidDataException("Invalid ONNX shape constant.");
                    shape = new long[raw.Length / 8];
                    for (int i = 0; i < shape.Length; i++)
                    {
                        var bytes = raw.Skip(i * 8).Take(8).ToArray();
                        if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
                        shape[i] = BitConverter.ToInt64(bytes, 0);
                    }
                }
                // In particular, exclude zero dimensions: ONNX zeros may copy a source
                // dimension, which would make removing Squeeze change reshape semantics.
                if (shape.Length == 0 || shape.Any(d => d == 0 || d < -1) || shape.Count(d => d == -1) > 1) continue;
                Set(node, "op_type", "Identity");
                List(node, "attribute").Clear();
                changed++;
            }
            using var stream = File.Create(destination);
            using var writer = new CodedOutputStream(stream);
            model.WriteTo(writer);
            return changed;
        }
    }
}
