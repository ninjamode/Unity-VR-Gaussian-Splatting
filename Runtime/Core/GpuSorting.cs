using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Rendering;

namespace Gaussians.Core
{
    // GPU (uint key, uint payload) 8 bit-LSD radix sort, using reduce-then-scan
    // Copyright Thomas Smith 2024, MIT license
    // https://github.com/b0nes164/GPUSorting

    public class GpuSorting
    {
        //The size of a threadblock partition in the sort
        const uint DEVICE_RADIX_SORT_PARTITION_SIZE = 3840;

        //The size of our radix in bits
        const uint DEVICE_RADIX_SORT_BITS = 8;

        //Number of digits in our radix, 1 << DEVICE_RADIX_SORT_BITS
        const uint DEVICE_RADIX_SORT_RADIX = 256;

        //Number of sorting passes required to sort a 32bit key, KEY_BITS / DEVICE_RADIX_SORT_BITS
        const uint DEVICE_RADIX_SORT_PASSES = 4;

        // Runtime choices; key/payload types, ascending order and pairs are fixed in HLSL.
        private LocalKeyword m_smallGroupsKeyword;
        private LocalKeyword m_vulkanKeyword;
        private LocalKeyword m_gpuCountKeyword;
        LocalKeywordSpace m_KeywordSpace;
        uint m_KeywordCount;
        GraphicsDeviceType m_DeviceType;
        bool m_UseSmallWorkgroups;
        bool m_HasGpuCountKeyword;
        readonly bool? m_SmallWorkgroupsOverride;
#if UNITY_EDITOR
        uint m_ShaderImportRevision;
#endif

        public struct Args
        {
            public uint             count;
            public GraphicsBuffer   inputKeys;
            public GraphicsBuffer   inputValues;
            public SupportResources resources;
            internal int workGroupCount;
        }

        public struct SupportResources
        {
            public GraphicsBuffer altBuffer;
            public GraphicsBuffer altPayloadBuffer;
            public GraphicsBuffer passHistBuffer;
            public GraphicsBuffer globalHistBuffer;

            public static SupportResources Load(uint count)
            {
                //This is threadBlocks * DEVICE_RADIX_SORT_RADIX
                uint scratchBufferSize = DivRoundUp(count, DEVICE_RADIX_SORT_PARTITION_SIZE) * DEVICE_RADIX_SORT_RADIX; 
                uint reducedScratchBufferSize = DEVICE_RADIX_SORT_RADIX * DEVICE_RADIX_SORT_PASSES;

                var target = GraphicsBuffer.Target.Structured;
                var resources = new SupportResources
                {
                    altBuffer = new GraphicsBuffer(target, (int)count, 4) { name = "DeviceRadixAlt" },
                    altPayloadBuffer = new GraphicsBuffer(target, (int)count, 4) { name = "DeviceRadixAltPayload" },
                    passHistBuffer = new GraphicsBuffer(target, (int)scratchBufferSize, 4) { name = "DeviceRadixPassHistogram" },
                    globalHistBuffer = new GraphicsBuffer(target, (int)reducedScratchBufferSize, 4) { name = "DeviceRadixGlobalHistogram" },
                };
                return resources;
            }

            public void Dispose()
            {
                altBuffer?.Dispose();
                altPayloadBuffer?.Dispose();
                passHistBuffer?.Dispose();
                globalHistBuffer?.Dispose();

                altBuffer = null;
                altPayloadBuffer = null;
                passHistBuffer = null;
                globalHistBuffer = null;
            }
        }

        readonly ComputeShader m_CS;
        int m_kernelInitDeviceRadixSort = -1;
        int m_kernelUpsweep = -1;
        int m_kernelScan = -1;
        int m_kernelDownsweep = -1;

        bool m_Valid;
        int m_InitDispatchGroups;

        public bool Valid
        {
            get { RefreshState(); return m_Valid; }
        }

        public GpuSorting(ComputeShader cs) : this(cs, null)
        {
        }

        internal GpuSorting(ComputeShader cs, bool useSmallWorkgroups) : this(cs, (bool?)useSmallWorkgroups)
        {
        }

        GpuSorting(ComputeShader cs, bool? useSmallWorkgroups)
        {
            m_CS = cs;
            m_SmallWorkgroupsOverride = useSmallWorkgroups;
            RefreshState();
        }

        void RefreshState()
        {
            if (!m_CS)
            {
                m_Valid = false;
                return;
            }

            var space = m_CS.keywordSpace;
            uint keywordCount = space.keywordCount;
            var deviceType = SystemInfo.graphicsDeviceType;
            bool smallGroups = m_SmallWorkgroupsOverride ??
                (SystemInfo.maxComputeWorkGroupSize < 1024 || SystemInfo.maxComputeWorkGroupSizeX < 1024);
            bool shaderUnchanged = space == m_KeywordSpace && keywordCount == m_KeywordCount;
#if UNITY_EDITOR
            shaderUnchanged &= m_ShaderImportRevision == GpuSortingShaderPostprocessor.Revision;
#endif
            // Reimports/platform switches can rebuild the keyword space without
            // replacing the ComputeShader object. Retry unsupported state as well,
            // since a query during shader import need not describe its final state.
            if (m_Valid && shaderUnchanged &&
                deviceType == m_DeviceType && smallGroups == m_UseSmallWorkgroups &&
                m_smallGroupsKeyword.isValid && m_vulkanKeyword.isValid &&
                (!m_HasGpuCountKeyword || m_gpuCountKeyword.isValid))
                return;

            m_KeywordSpace = space;
            m_KeywordCount = keywordCount;
#if UNITY_EDITOR
            m_ShaderImportRevision = GpuSortingShaderPostprocessor.Revision;
#endif
            m_DeviceType = deviceType;
            m_UseSmallWorkgroups = smallGroups;
            m_smallGroupsKeyword = space.FindKeyword("GAUSSIANS_SMALL_WORKGROUPS");
            m_vulkanKeyword = space.FindKeyword("VULKAN");
            // Optional: the 2D shader uses only CPU-sized dispatches.
            m_gpuCountKeyword = space.FindKeyword("GAUSSIANS_GPU_SORT_COUNT");
            m_HasGpuCountKeyword = m_gpuCountKeyword.isValid;

            // Select the supported variant before querying kernel support. This
            // setting also applies to the utility kernels in the same shader.
            if (m_smallGroupsKeyword.isValid) m_CS.SetKeyword(m_smallGroupsKeyword, smallGroups);
            if (m_vulkanKeyword.isValid) m_CS.SetKeyword(m_vulkanKeyword, deviceType == GraphicsDeviceType.Vulkan);
            m_InitDispatchGroups = smallGroups ? 4 : 1;
            m_kernelInitDeviceRadixSort = FindKernel("InitDeviceRadixSort");
            m_kernelUpsweep = FindKernel("Upsweep");
            m_kernelScan = FindKernel("Scan");
            m_kernelDownsweep = FindKernel("Downsweep");

            m_Valid = m_smallGroupsKeyword.isValid && m_vulkanKeyword.isValid &&
                      m_kernelInitDeviceRadixSort >= 0 &&
                      m_kernelUpsweep >= 0 &&
                      m_kernelScan >= 0 &&
                      m_kernelDownsweep >= 0;
            if (m_Valid)
            {
                if (!m_CS.IsSupported(m_kernelInitDeviceRadixSort) ||
                    !m_CS.IsSupported(m_kernelUpsweep) ||
                    !m_CS.IsSupported(m_kernelScan) ||
                    !m_CS.IsSupported(m_kernelDownsweep))
                {
                    m_Valid = false;
                }
            }
        }

        int FindKernel(string name) => m_CS.HasKernel(name) ? m_CS.FindKernel(name) : -1;

        static uint DivRoundUp(uint x, uint y) => (x + y - 1) / y;

        // Match the shader constant buffer's 16-byte layout, including its unused fourth word.
        struct SortConstants
        {
            public uint numKeys;                        // The number of keys to sort
            public uint radixShift;                     // The radix shift value for the current pass
            public uint threadBlocks;                   // threadBlocks
            public uint padding0;                       // Padding - unused
        }

        public void Dispatch(CommandBuffer cmd, Args args) => Dispatch(cmd, args, 32);

        // Sort the most significant key bits. For an odd pass count the caller
        // generates input in the alternate buffers, so output always lands in
        // inputKeys/inputValues without a full-buffer copy or rebinding draws.
        public void Dispatch(CommandBuffer cmd, Args args, int keyBits, GraphicsBuffer gpuCounts = null)
        {
            RefreshState();
            Assert.IsTrue(m_Valid);
            if (keyBits != 16 && keyBits != 24 && keyBits != 32)
                throw new System.ArgumentOutOfRangeException(nameof(keyBits));
            if (args.count == 0) return;
            if (gpuCounts != null && !m_HasGpuCountKeyword)
                throw new System.InvalidOperationException("This sorter does not support GPU counts.");
            // Record the variant for this dispatch, even when another sorter shares
            // the shader or an import reset its keyword state.
            cmd.SetKeyword(m_CS, m_smallGroupsKeyword, m_UseSmallWorkgroups);
            cmd.SetKeyword(m_CS, m_vulkanKeyword, m_DeviceType == GraphicsDeviceType.Vulkan);
            if (m_HasGpuCountKeyword)
                cmd.SetKeyword(m_CS, m_gpuCountKeyword, gpuCounts != null);
            if (gpuCounts != null)
            {
                cmd.SetComputeBufferParam(m_CS, m_kernelUpsweep, "b_sortCounts", gpuCounts);
                cmd.SetComputeBufferParam(m_CS, m_kernelScan, "b_sortCounts", gpuCounts);
                cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_sortCounts", gpuCounts);
            }
            bool alternateInput = keyBits == 24;

            GraphicsBuffer srcKeyBuffer = alternateInput ? args.resources.altBuffer : args.inputKeys;
            GraphicsBuffer srcPayloadBuffer = alternateInput ? args.resources.altPayloadBuffer : args.inputValues;
            GraphicsBuffer dstKeyBuffer = alternateInput ? args.inputKeys : args.resources.altBuffer;
            GraphicsBuffer dstPayloadBuffer = alternateInput ? args.inputValues : args.resources.altPayloadBuffer;

            SortConstants constants = default;
            constants.numKeys = args.count;
            constants.threadBlocks = DivRoundUp(args.count, DEVICE_RADIX_SORT_PARTITION_SIZE);

            // Setup overall constants
            cmd.SetComputeIntParam(m_CS, "e_numKeys", (int)constants.numKeys);
            cmd.SetComputeIntParam(m_CS, "e_threadBlocks", (int)constants.threadBlocks);

            //Set statically located buffers
            //Upsweep
            cmd.SetComputeBufferParam(m_CS, m_kernelUpsweep, "b_passHist", args.resources.passHistBuffer);
            cmd.SetComputeBufferParam(m_CS, m_kernelUpsweep, "b_globalHist", args.resources.globalHistBuffer);

            //Scan
            cmd.SetComputeBufferParam(m_CS, m_kernelScan, "b_passHist", args.resources.passHistBuffer);

            //Downsweep
            cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_passHist", args.resources.passHistBuffer);
            cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_globalHist", args.resources.globalHistBuffer);

            //Clear the global histogram
            cmd.SetComputeBufferParam(m_CS, m_kernelInitDeviceRadixSort, "b_globalHist", args.resources.globalHistBuffer);
            // Always clear all 4 x 256 histogram entries in either variant.
            cmd.DispatchCompute(m_CS, m_kernelInitDeviceRadixSort, m_InitDispatchGroups, 1, 1);

            // Execute the sort algorithm in 8-bit increments
            for (constants.radixShift = (uint)(32 - keyBits); constants.radixShift < 32; constants.radixShift += DEVICE_RADIX_SORT_BITS)
            {
                cmd.SetComputeIntParam(m_CS, "e_radixShift", (int)constants.radixShift);

                //Upsweep
                cmd.SetComputeBufferParam(m_CS, m_kernelUpsweep, "b_sort", srcKeyBuffer);
                if (gpuCounts != null) cmd.DispatchCompute(m_CS, m_kernelUpsweep, gpuCounts, 8);
                else cmd.DispatchCompute(m_CS, m_kernelUpsweep, (int)constants.threadBlocks, 1, 1);

                // Scan
                cmd.DispatchCompute(m_CS, m_kernelScan, (int)DEVICE_RADIX_SORT_RADIX, 1, 1);

                // Downsweep
                cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_sort", srcKeyBuffer);
                cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_sortPayload", srcPayloadBuffer);
                cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_alt", dstKeyBuffer);
                cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_altPayload", dstPayloadBuffer);
                if (gpuCounts != null) cmd.DispatchCompute(m_CS, m_kernelDownsweep, gpuCounts, 8);
                else cmd.DispatchCompute(m_CS, m_kernelDownsweep, (int)constants.threadBlocks, 1, 1);

                // Swap
                (srcKeyBuffer, dstKeyBuffer) = (dstKeyBuffer, srcKeyBuffer);
                (srcPayloadBuffer, dstPayloadBuffer) = (dstPayloadBuffer, srcPayloadBuffer);
            }
        }
    }

#if UNITY_EDITOR
    // Unity may preserve/reuse a keyword-space pointer on import, including when
    // only kernel ordinals change. An import revision avoids per-dispatch asset
    // database queries and ensures cached kernel/support state is refreshed too.
    sealed class GpuSortingShaderPostprocessor : UnityEditor.AssetPostprocessor
    {
        internal static uint Revision { get; private set; }

        static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
            string[] movedAssets, string[] movedFromAssetPaths)
        {
            foreach (string path in importedAssets)
            {
                if (!path.EndsWith(".compute", System.StringComparison.OrdinalIgnoreCase)) continue;
                ++Revision;
                return;
            }
        }
    }
#endif
}
