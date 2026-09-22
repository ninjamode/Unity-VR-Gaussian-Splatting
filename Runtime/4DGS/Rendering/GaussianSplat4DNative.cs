using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Gaussians.FourD
{
    /// <summary>Persistent HexPlane sampling and tiled FP32 dense layers, using the portable export contract.</summary>
    public sealed class GaussianSplat4DNative : IDisposable
    {
        [Serializable] sealed class Config
        {
            public bool no_grid, static_mlp, empty_voxel, no_dx, no_ds, no_dr, no_do, no_dshs, apply_rotation;
            public int grid_pe;
            public int[] multires;
        }
        [Serializable] sealed class Canonical { public string positions, log_scales, rotations_wxyz, opacity_logits, sh; }
        [Serializable] sealed class Manifest { public Config configuration; public Canonical canonical; }
        struct Layer { public int Input, Output, Weight, Bias; }
        readonly List<ComputeBuffer> m_Buffers = new();
        readonly List<Layer> m_Stem = new();
        readonly Layer[,] m_Heads = new Layer[5,2];
        readonly ComputeBuffer[] m_Canonical = new ComputeBuffer[5];
        readonly int[] m_Offsets = {0,3,6,10,11};
        ComputeShader m_Shader;
        ComputeBuffer m_Parameters,m_Planes,m_Spatial,m_A,m_B,m_HeadHidden,m_Deltas;
        int m_Features,m_Channels,m_Active,m_DeltaWidth;
        bool m_HasSH,m_LastDeform;
        public GaussianSplat4DGpuData Data { get; private set; }
        public Bounds CanonicalBounds { get; private set; }

        public GaussianSplat4DNative(GaussianSplat4DAsset asset,ComputeShader shader,ComputeShader activationShader, Gaussians.ThreeD.GaussianSplatSHStorage storage = Gaussians.ThreeD.GaussianSplatSHStorage.Float32)
        {
            if(!asset || !shader)throw new ArgumentException("Native deformation requires data and shader.");
            try
            {
                var manifest=JsonUtility.FromJson<Manifest>(asset.Manifest.text);
                var config=manifest.configuration;
                if(config==null || config.no_grid || config.static_mlp || config.empty_voxel || config.grid_pe!=0 || config.multires==null || config.multires.Length==0)
                    throw new NotSupportedException("Native 4DGS supports the exporter's six-plane, grid_pe=0, non-static MLP contract.");
                m_Shader=UnityEngine.Object.Instantiate(shader);
                m_Shader.hideFlags=HideFlags.HideAndDontSave;
                foreach(string kernel in new[]{"CacheSpatial","Features","Dense","Compose"})
                    if(!m_Shader.IsSupported(m_Shader.FindKernel(kernel)))
                        throw new NotSupportedException("Native 4DGS kernel is unsupported: "+kernel);
                int count=asset.GaussianCount, coefficients=(asset.SHDegree+1)*(asset.SHDegree+1);
                m_DeltaWidth=11+coefficients*3;
                Data=new GaussianSplat4DGpuData(count,coefficients,activationShader,storage);
                var names=manifest.canonical;
                string[] canonical={names.positions,names.log_scales,names.rotations_wxyz,names.opacity_logits,names.sh};
                int[] widths={3,3,4,1,coefficients*3};
                for(int i=0;i<5;i++)
                {
                    float[] values=asset.GetTensor(canonical[i]).ReadFloats();
                    if(values.Length!=checked(count*widths[i]))throw new InvalidDataException("Invalid canonical shape: "+canonical[i]);
                    m_Canonical[i]=Upload(values);
                    if(i==0)
                    {
                        var bounds=new Bounds(new Vector3(values[0],values[1],values[2]),Vector3.zero);
                        for(int p=3;p<values.Length;p+=3)bounds.Encapsulate(new Vector3(values[p],values[p+1],values[p+2]));
                        CanonicalBounds=bounds;
                    }
                }
                var parameters=new List<float>();
                var planes=new List<Vector4Int>();
                for(int level=0;level<config.multires.Length;level++)for(int plane=0;plane<6;plane++)
                {
                    var tensor=asset.GetTensor("weights/deformation_net.grid.grids."+level+"."+plane);
                    var shape=tensor.shape;
                    if(shape.Length!=4 || shape[0]!=1 || shape[1]<1 || shape[2]<2 || shape[3]<2)
                        throw new InvalidDataException("Invalid plane dimensions: "+tensor.name);
                    if(level==0 && plane==0)m_Channels=shape[1];
                    if(shape[1]!=m_Channels)throw new NotSupportedException("All levels must use the same feature channel count.");
                    planes.Add(new Vector4Int(parameters.Count,shape[3],shape[2],shape[1]));
                    parameters.AddRange(tensor.ReadFloats());
                }
                m_Features=checked(m_Channels*config.multires.Length);
                Layer ReadLayer(string name)
                {
                    var weight=asset.GetTensor("weights/deformation_net."+name+".weight");
                    var bias=asset.GetTensor("weights/deformation_net."+name+".bias");
                    if(weight.shape.Length!=2 || weight.shape[0]<1 || weight.shape[1]<1 || bias.shape.Length!=1 || bias.shape[0]!=weight.shape[0])
                        throw new InvalidDataException("Invalid dense layer: "+name);
                    var layer=new Layer{Input=weight.shape[1],Output=weight.shape[0],Weight=parameters.Count};
                    parameters.AddRange(weight.ReadFloats());layer.Bias=parameters.Count;parameters.AddRange(bias.ReadFloats());return layer;
                }
                var indices=asset.Tensors.Where(t=>t.name.StartsWith("weights/deformation_net.feature_out.",StringComparison.Ordinal)&&t.name.EndsWith(".weight",StringComparison.Ordinal))
                    .Select(t=>int.Parse(t.name.Split('.')[2])).OrderBy(i=>i).ToArray();
                int previous=m_Features,maxWidth=m_Features;
                if(indices.Length==0)throw new InvalidDataException("Missing feature MLP.");
                foreach(int index in indices)
                {
                    var layer=ReadLayer("feature_out."+index);
                    if(layer.Input!=previous)throw new InvalidDataException("Feature MLP dimensions do not connect.");
                    previous=layer.Output;maxWidth=Math.Max(maxWidth,previous);m_Stem.Add(layer);
                }
                bool[] active={!config.no_dx,!config.no_ds,!config.no_dr,!config.no_do,!config.no_dshs};
                string[] heads={"pos_deform","scales_deform","rotations_deform","opacity_deform","shs_deform"};
                for(int h=0;h<5;h++)if(active[h])
                {
                    m_Active|=1<<h;
                    m_Heads[h,0]=ReadLayer(heads[h]+".1");m_Heads[h,1]=ReadLayer(heads[h]+".3");
                    if(m_Heads[h,0].Input!=previous || m_Heads[h,1].Input!=m_Heads[h,0].Output || m_Heads[h,1].Output!=widths[h])
                        throw new InvalidDataException("Invalid deformation head dimensions: "+heads[h]);
                    maxWidth=Math.Max(maxWidth,m_Heads[h,0].Output);
                }
                m_Parameters=Upload(parameters.ToArray());
                m_Planes=Allocate(planes.Count,16);m_Planes.SetData(planes);
                m_Spatial=Allocate(checked(count*m_Features),8);
                m_A=Allocate(checked(count*maxWidth));m_B=Allocate(checked(count*maxWidth));
                m_HeadHidden=Allocate(checked(count*maxWidth));m_Deltas=Allocate(checked(count*m_DeltaWidth));
                var aabb=asset.GetTensor("weights/deformation_net.grid.aabb").ReadFloats();
                if(aabb.Length!=6 || !(aabb[0]>aabb[3] && aabb[1]>aabb[4] && aabb[2]>aabb[5]))throw new InvalidDataException("Invalid network AABB.");
                m_Shader.SetVector("_AabbMax",new Vector4(aabb[0],aabb[1],aabb[2],0));
                m_Shader.SetVector("_NormalizeScale",new Vector4(2/(aabb[3]-aabb[0]),2/(aabb[4]-aabb[1]),2/(aabb[5]-aabb[2]),0));
                m_Shader.SetInt("_Count",count);m_Shader.SetInt("_FeatureWidth",m_Features);m_Shader.SetInt("_Channels",m_Channels);
                m_Shader.SetInt("_SHStorage",(int)storage);
                m_Shader.SetInt("_CoefficientCount",coefficients);m_Shader.SetInt("_ActiveHeads",m_Active);m_Shader.SetInt("_ApplyRotation",config.apply_rotation?1:0);
                foreach(string name in new[]{"CacheSpatial","Features"})
                {
                    int k=m_Shader.FindKernel(name);
                    m_Shader.SetBuffer(k,"_Parameters",m_Parameters);m_Shader.SetBuffer(k,"_Planes",m_Planes);
                    m_Shader.SetBuffer(k,"_Positions",m_Canonical[0]);m_Shader.SetBuffer(k,"_Spatial",m_Spatial);
                }
                int dense=m_Shader.FindKernel("Dense");m_Shader.SetBuffer(dense,"_Parameters",m_Parameters);
                int compose=m_Shader.FindKernel("Compose");
                string[] inputs={"_Positions","_LogScales","_Rotations","_OpacityLogits","_RawSH"};
                for(int i=0;i<5;i++)m_Shader.SetBuffer(compose,inputs[i],m_Canonical[i]);
                m_Shader.SetBuffer(compose,"_Deltas",m_Deltas);m_Shader.SetBuffer(compose,"_Splats",Data.Splats);m_Shader.SetBuffer(compose,"_SH",Data.SH);
                DispatchLinear(m_Shader.FindKernel("CacheSpatial"),checked(count*m_Features));
            }
            catch{Dispose();throw;}
        }

        // Blittable integer metadata; Unity has no Vector4Int.
        struct Vector4Int { public int x,y,z,w; public Vector4Int(int a,int b,int c,int d){x=a;y=b;z=c;w=d;} }
        ComputeBuffer Allocate(int count,int stride=4){var b=new ComputeBuffer(count,stride);m_Buffers.Add(b);return b;}
        ComputeBuffer Upload(float[] data){var b=Allocate(data.Length);b.SetData(data);return b;}
        void DispatchLinear(int kernel,int count)
        {
            int groups=(int)((count+63L)/64), x=Math.Min(groups,65535);
            m_Shader.SetInt("_LinearWidth",x*64);
            m_Shader.Dispatch(kernel,x,(groups+65534)/65535,1);
        }
        void Dense(Layer layer,ComputeBuffer input,ComputeBuffer output,bool relu,int stride,int offset=0)
        {
            int k=m_Shader.FindKernel("Dense");
            m_Shader.SetInt("_InputWidth",layer.Input);m_Shader.SetInt("_OutputWidth",layer.Output);
            m_Shader.SetInt("_WeightOffset",layer.Weight);m_Shader.SetInt("_BiasOffset",layer.Bias);
            m_Shader.SetInt("_OutputStride",stride);m_Shader.SetInt("_OutputOffset",offset);m_Shader.SetInt("_Relu",relu?1:0);
            m_Shader.SetBuffer(k,"_Input",input);m_Shader.SetBuffer(k,"_Output",output);
            int pointGroups=(Data.Count+7)/8;
            m_Shader.Dispatch(k,(layer.Output+15)/16,Math.Min(pointGroups,65535),(pointGroups+65534)/65535);
        }
        public void Evaluate(float time,bool deform)
        {
            if(!m_Shader)throw new ObjectDisposedException(nameof(GaussianSplat4DNative));
            if(!float.IsFinite(time))throw new ArgumentOutOfRangeException(nameof(time));
            if(deform && m_Active!=0)
            {
                int features=m_Shader.FindKernel("Features");m_Shader.SetFloat("_Time",time);m_Shader.SetBuffer(features,"_Output",m_A);
                DispatchLinear(features,checked(Data.Count*m_Features));
                ComputeBuffer input=m_A,output=m_B;
                for(int i=0;i<m_Stem.Count;i++){Dense(m_Stem[i],input,output,i!=0,m_Stem[i].Output);(input,output)=(output,input);}
                for(int h=0;h<5;h++)if((m_Active&(1<<h))!=0)
                {
                    Dense(m_Heads[h,0],input,m_HeadHidden,true,m_Heads[h,0].Output);
                    Dense(m_Heads[h,1],m_HeadHidden,m_Deltas,true,m_DeltaWidth,m_Offsets[h]);
                }
            }
            m_Shader.SetInt("_Deform",deform?1:0);
            m_Shader.SetInt("_WriteSH",!m_HasSH || (m_Active&16)!=0 || deform!=m_LastDeform?1:0);
            DispatchLinear(m_Shader.FindKernel("Compose"),Data.Count);
            m_HasSH=true;m_LastDeform=deform;
        }
        public void Dispose()
        {
            Data?.Dispose();Data=null;
            foreach(var b in m_Buffers)b.Dispose();m_Buffers.Clear();
            if(m_Shader){if(Application.isPlaying)UnityEngine.Object.Destroy(m_Shader);else UnityEngine.Object.DestroyImmediate(m_Shader);}
            m_Shader=null;
        }
    }
}
