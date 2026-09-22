using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Gaussians.FourD.Editor
{
    public static partial class GaussianSplat4DBundleImporter
    {
        static void CheckNativeStructure(JObject json, string root)
        {
            var config = json["configuration"];
            if(config==null || (bool?)config["no_grid"]==true || (bool?)config["static_mlp"]==true || (bool?)config["empty_voxel"]==true ||
                (int?)config["grid_pe"] is int pe && pe!=0 || config?["multires"] is not JArray levels || levels.Count==0)
                throw new InvalidDataException("Native backend requires a non-static six-plane model with grid_pe=0. Select ONNX for other supported exports.");
            int[] Shape(string name)
            {
                var tensor=json["tensors"]?["weights/deformation_net."+name];
                if(tensor==null || (string)tensor["dtype"]!="<f4") throw new InvalidDataException("Missing FP32 native tensor: "+name);
                return tensor["shape"].ToObject<int[]>();
            }
            int channels=0;
            for(int level=0;level<levels.Count;level++)for(int plane=0;plane<6;plane++)
            {
                var shape=Shape($"grid.grids.{level}.{plane}");
                if(shape.Length!=4 || shape[0]!=1 || shape[1]<1 || shape[2]<2 || shape[3]<2) throw new InvalidDataException("Invalid native plane dimensions.");
                if(channels==0)channels=shape[1];
                if(channels!=shape[1])throw new InvalidDataException("Native planes must have equal channel counts.");
            }
            if(!Shape("grid.aabb").SequenceEqual(new[]{2,3}))throw new InvalidDataException("Native AABB must have shape [2,3].");
            var aabbBytes=File.ReadAllBytes(CheckedPath(root,(string)json["tensors"]["weights/deformation_net.grid.aabb"]["path"]));
            var aabb=new float[6];Buffer.BlockCopy(aabbBytes,0,aabb,0,24);
            if(aabb.Any(v=>!float.IsFinite(v)) || !(aabb[0]>aabb[3] && aabb[1]>aabb[4] && aabb[2]>aabb[5]))
                throw new InvalidDataException("Native AABB must have finite nonzero extents.");
            int Layer(string name,int input)
            {
                var weight=Shape(name+".weight");var bias=Shape(name+".bias");
                if(weight.Length!=2 || weight[0]<=0 || weight[1]!=input || bias.Length!=1 || bias[0]!=weight[0])
                    throw new InvalidDataException("Disconnected or invalid native layer: "+name);
                return weight[0];
            }
            var layers=((JObject)json["tensors"]).Properties().Where(p=>p.Name.StartsWith("weights/deformation_net.feature_out.",StringComparison.Ordinal)&&p.Name.EndsWith(".weight",StringComparison.Ordinal))
                .Select(p=>int.Parse(p.Name.Split('.')[2])).OrderBy(i=>i).ToArray();
            if(layers.Length==0)throw new InvalidDataException("Missing native feature MLP.");
            int width=checked(channels*levels.Count);
            foreach(int layer in layers)width=Layer("feature_out."+layer,width);
            string[] heads={"pos_deform","scales_deform","rotations_deform","opacity_deform","shs_deform"};
            string[] flags={"no_dx","no_ds","no_dr","no_do","no_dshs"};
            int degree=(int)json["sh_degree"];int[] outputs={3,3,4,1,(degree+1)*(degree+1)*3};
            for(int h=0;h<5;h++)if((bool?)config[flags[h]]!=true)
            {
                int hidden=Layer(heads[h]+".1",width);
                if(Layer(heads[h]+".3",hidden)!=outputs[h])throw new InvalidDataException("Invalid native output width: "+heads[h]);
            }
        }
    }
}
