using System;
using System.IO;
using UnityEngine;

namespace Gaussians.FourD.Editor
{
    public static class Gaussian4DMortonOrder
    {
        // New index -> original index. Stable tie breaking makes reimports deterministic.
        public static int[] Create(float[] positions)
        {
            if (positions.Length == 0 || positions.Length % 3 != 0) throw new InvalidDataException("Invalid positions.");
            int count = positions.Length / 3;
            var min = new Vector3(float.PositiveInfinity,float.PositiveInfinity,float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity,float.NegativeInfinity,float.NegativeInfinity);
            for (int i=0;i<count;i++)
            {
                var p = new Vector3(positions[i*3],positions[i*3+1],positions[i*3+2]);
                if (!float.IsFinite(p.x)||!float.IsFinite(p.y)||!float.IsFinite(p.z)) throw new InvalidDataException("Nonfinite canonical position.");
                min=Vector3.Min(min,p); max=Vector3.Max(max,p);
            }
            var keys = new ulong[count]; var order = new int[count];
            uint Quantize(float v, float lo, float hi) => hi > lo ? (uint)Math.Clamp(((double)v-lo)/((double)hi-lo)*2097151,0,2097151) : 0;
            for(int i=0;i<count;i++)
            {
                uint x=Quantize(positions[i*3],min.x,max.x), y=Quantize(positions[i*3+1],min.y,max.y), z=Quantize(positions[i*3+2],min.z,max.z);
                ulong key=0;
                for(int bit=0;bit<21;bit++) key |= ((ulong)((x>>bit)&1)<<(bit*3)) | ((ulong)((y>>bit)&1)<<(bit*3+1)) | ((ulong)((z>>bit)&1)<<(bit*3+2));
                keys[i]=key;order[i]=i;
            }
            Array.Sort(order,(a,b)=>keys[a]!=keys[b]?keys[a].CompareTo(keys[b]):a.CompareTo(b));
            return order;
        }
        public static byte[] Apply(byte[] input, int[] order, int stride)
        {
            if(input.Length != checked(order.Length*stride)) throw new InvalidDataException("Canonical tensor count mismatch.");
            var output=new byte[input.Length];
            for(int i=0;i<order.Length;i++) Buffer.BlockCopy(input,checked(order[i]*stride),output,i*stride,stride);
            return output;
        }
    }
}
