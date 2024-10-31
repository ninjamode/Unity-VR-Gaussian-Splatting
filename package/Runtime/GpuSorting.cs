using UnityEditor;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    // GPU (uint key, uint payload) 8 bit-LSD radix sort, using reduce-then-scan
    // Copyright Thomas Smith 2024, MIT license
    // https://github.com/b0nes164/GPUSorting

    public class GpuSorting
    {
        public enum SortType
        {
            None = 0,
            DeviceRadixSort,
            FidelityFX
        }

        public GpuSorting(){}

        public GpuSorting(SortType sortType, ComputeShader cs)
        {
            Create(sortType, cs);
        }
        

        public SortType activeType = SortType.None;
        
        private DeviceRadixSort _deviceRadixSort;
        private DeviceRadixSort.Args _drsSorterArgs;

        private FidelityFxSort _fidelityFxSort;
        private FidelityFxSort.Args _ffxSorterArgs;

        public void Create(SortType sortType, ComputeShader cs)
        {
            if (sortType == SortType.DeviceRadixSort)
            {
                _deviceRadixSort = new DeviceRadixSort(cs);
            }
            else if (sortType == SortType.FidelityFX)
            {
                _fidelityFxSort = new FidelityFxSort(cs);
            }
            activeType = sortType;
        }

        public void Initialize(uint count, GraphicsBuffer inputKeys, GraphicsBuffer inputValues)
        {
            if (activeType == SortType.DeviceRadixSort)
            {
                _drsSorterArgs.resources.Dispose();
                _drsSorterArgs.inputKeys = inputKeys;
                _drsSorterArgs.inputValues = inputValues;
                _drsSorterArgs.count = count;
                if (_deviceRadixSort.Valid) _drsSorterArgs.resources = DeviceRadixSort.SupportResources.Load(count);
            }
            else if (activeType == SortType.FidelityFX)
            {
                _ffxSorterArgs.resources.Dispose();
                _ffxSorterArgs.inputKeys = inputKeys;
                _ffxSorterArgs.inputValues = inputValues;
                _ffxSorterArgs.count = count;
                if (_fidelityFxSort.Valid) _ffxSorterArgs.resources = FidelityFxSort.SupportResources.Load(count);
            }
        }

        public void DisposeResources()
        {
            if (activeType == SortType.DeviceRadixSort)
            {
                _drsSorterArgs.resources.Dispose();
            }
            else if (activeType == SortType.FidelityFX)
            {
                _ffxSorterArgs.resources.Dispose();
            }
        }

        public void Dispatch(CommandBuffer cmd)
        {
            if (activeType == SortType.DeviceRadixSort)
            {
                _deviceRadixSort.Dispatch(cmd, _drsSorterArgs);
            }
            else if (activeType == SortType.FidelityFX)
            {
                _fidelityFxSort.Dispatch(cmd, _ffxSorterArgs);
            }
        }
        
    }
    
    
    
    public class DeviceRadixSort
    {
        //The size of a threadblock partition in the sort
        const uint DEVICE_RADIX_SORT_PARTITION_SIZE = 3840;

        //The size of our radix in bits
        const uint DEVICE_RADIX_SORT_BITS = 8;

        //Number of digits in our radix, 1 << DEVICE_RADIX_SORT_BITS
        const uint DEVICE_RADIX_SORT_RADIX = 256;

        //Number of sorting passes required to sort a 32bit key, KEY_BITS / DEVICE_RADIX_SORT_BITS
        const uint DEVICE_RADIX_SORT_PASSES = 4;

        //Keywords to enable for the shader
        private LocalKeyword m_keyUintKeyword;
        private LocalKeyword m_payloadUintKeyword;
        private LocalKeyword m_ascendKeyword;
        private LocalKeyword m_sortPairKeyword;
        private LocalKeyword m_vulkanKeyword;

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
        readonly int m_kernelInitDeviceRadixSort = -1;
        readonly int m_kernelUpsweep = -1;
        readonly int m_kernelScan = -1;
        readonly int m_kernelDownsweep = -1;

        readonly bool m_Valid;

        public bool Valid => m_Valid;

        public DeviceRadixSort(ComputeShader cs)
        {
            m_CS = cs;
            if (cs)
            {
                m_kernelInitDeviceRadixSort = cs.FindKernel("InitDeviceRadixSort");
                m_kernelUpsweep = cs.FindKernel("Upsweep");
                m_kernelScan = cs.FindKernel("Scan");
                m_kernelDownsweep = cs.FindKernel("Downsweep");
            }

            m_Valid = m_kernelInitDeviceRadixSort >= 0 &&
                      m_kernelUpsweep >= 0 &&
                      m_kernelScan >= 0 &&
                      m_kernelDownsweep >= 0;
            if (m_Valid)
            {
                if (!cs.IsSupported(m_kernelInitDeviceRadixSort) ||
                    !cs.IsSupported(m_kernelUpsweep) ||
                    !cs.IsSupported(m_kernelScan) ||
                    !cs.IsSupported(m_kernelDownsweep))
                {
                    m_Valid = false;
                }
            }

            m_keyUintKeyword = new LocalKeyword(cs, "KEY_UINT");
            m_payloadUintKeyword = new LocalKeyword(cs, "PAYLOAD_UINT");
            m_ascendKeyword = new LocalKeyword(cs, "SHOULD_ASCEND");
            m_sortPairKeyword = new LocalKeyword(cs, "SORT_PAIRS");
            m_vulkanKeyword = new LocalKeyword(cs, "VULKAN");

            cs.EnableKeyword(m_keyUintKeyword);
            cs.EnableKeyword(m_payloadUintKeyword);
            cs.EnableKeyword(m_ascendKeyword);
            cs.EnableKeyword(m_sortPairKeyword);
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Vulkan)
                cs.EnableKeyword(m_vulkanKeyword);
            else
                cs.DisableKeyword(m_vulkanKeyword);
        }

        static uint DivRoundUp(uint x, uint y) => (x + y - 1) / y;

        //Can we remove the last 4 padding without breaking?
        struct SortConstants
        {
            public uint numKeys;                        // The number of keys to sort
            public uint radixShift;                     // The radix shift value for the current pass
            public uint threadBlocks;                   // threadBlocks
            public uint padding0;                       // Padding - unused
        }

        public void Dispatch(CommandBuffer cmd, Args args)
        {
            Assert.IsTrue(Valid);

            GraphicsBuffer srcKeyBuffer = args.inputKeys;
            GraphicsBuffer srcPayloadBuffer = args.inputValues;
            GraphicsBuffer dstKeyBuffer = args.resources.altBuffer;
            GraphicsBuffer dstPayloadBuffer = args.resources.altPayloadBuffer;

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
            cmd.DispatchCompute(m_CS, m_kernelInitDeviceRadixSort, 1, 1, 1);

            // Execute the sort algorithm in 8-bit increments
            for (constants.radixShift = 0; constants.radixShift < 32; constants.radixShift += DEVICE_RADIX_SORT_BITS)
            {
                cmd.SetComputeIntParam(m_CS, "e_radixShift", (int)constants.radixShift);

                //Upsweep
                cmd.SetComputeBufferParam(m_CS, m_kernelUpsweep, "b_sort", srcKeyBuffer);
                cmd.DispatchCompute(m_CS, m_kernelUpsweep, (int)constants.threadBlocks, 1, 1);

                // Scan
                cmd.DispatchCompute(m_CS, m_kernelScan, (int)DEVICE_RADIX_SORT_RADIX, 1, 1);

                // Downsweep
                cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_sort", srcKeyBuffer);
                cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_sortPayload", srcPayloadBuffer);
                cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_alt", dstKeyBuffer);
                cmd.SetComputeBufferParam(m_CS, m_kernelDownsweep, "b_altPayload", dstPayloadBuffer);
                cmd.DispatchCompute(m_CS, m_kernelDownsweep, (int)constants.threadBlocks, 1, 1);

                // Swap
                (srcKeyBuffer, dstKeyBuffer) = (dstKeyBuffer, srcKeyBuffer);
                (srcPayloadBuffer, dstPayloadBuffer) = (dstPayloadBuffer, srcPayloadBuffer);
            }
        }
    }
    
    
    // GPU (uint key, uint payload) radix sort, originally based on code derived from AMD FidelityFX SDK:
    // Copyright © 2023 Advanced Micro Devices, Inc., MIT license
    // https://github.com/GPUOpen-Effects/FidelityFX-ParallelSort v1.1.1
    public class FidelityFxSort
    {
        // These need to match constants in the compute shader
        const uint FFX_PARALLELSORT_ELEMENTS_PER_THREAD = 4;
        const uint FFX_PARALLELSORT_THREADGROUP_SIZE = 128;
        const int FFX_PARALLELSORT_SORT_BITS_PER_PASS = 4;
        const uint FFX_PARALLELSORT_SORT_BIN_COUNT = 1u << FFX_PARALLELSORT_SORT_BITS_PER_PASS;
        // The maximum number of thread groups to run in parallel. Modifying this value can help or hurt GPU occupancy,
        // but is very hardware class specific
        const uint FFX_PARALLELSORT_MAX_THREADGROUPS_TO_RUN = 800;

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
            public GraphicsBuffer sortScratchBuffer;
            public GraphicsBuffer payloadScratchBuffer;
            public GraphicsBuffer scratchBuffer;
            public GraphicsBuffer reducedScratchBuffer;

            public static SupportResources Load(uint count)
            {
                uint BlockSize = FFX_PARALLELSORT_ELEMENTS_PER_THREAD * FFX_PARALLELSORT_THREADGROUP_SIZE;
                uint NumBlocks = DivRoundUp(count, BlockSize);
                uint NumReducedBlocks = DivRoundUp(NumBlocks, BlockSize);

                uint scratchBufferSize = FFX_PARALLELSORT_SORT_BIN_COUNT * NumBlocks;
                uint reduceScratchBufferSize = FFX_PARALLELSORT_SORT_BIN_COUNT * NumReducedBlocks;

                var target = GraphicsBuffer.Target.Structured;
                var resources = new SupportResources
                {
                    sortScratchBuffer = new GraphicsBuffer(target, (int)count, 4) { name = "FfxSortSortScratch" },
                    payloadScratchBuffer = new GraphicsBuffer(target, (int)count, 4) { name = "FfxSortPayloadScratch" },
                    scratchBuffer = new GraphicsBuffer(target, (int)scratchBufferSize, 4) { name = "FfxSortScratch" },
                    reducedScratchBuffer = new GraphicsBuffer(target, (int)reduceScratchBufferSize, 4) { name = "FfxSortReducedScratch" },
                };
                return resources;
            }

            public void Dispose()
            {
                sortScratchBuffer?.Dispose();
                payloadScratchBuffer?.Dispose();
                scratchBuffer?.Dispose();
                reducedScratchBuffer?.Dispose();

                sortScratchBuffer = null;
                payloadScratchBuffer = null;
                scratchBuffer = null;
                reducedScratchBuffer = null;
            }
        }

        readonly ComputeShader m_CS;
        readonly int m_KernelReduce = -1;
        readonly int m_KernelScanAdd = -1;
        readonly int m_KernelScan = -1;
        readonly int m_KernelScatter = -1;
        readonly int m_KernelSum = -1;
        readonly bool m_Valid;

        public bool Valid => m_Valid;

        public FidelityFxSort(ComputeShader cs)
        {
            m_CS = cs;
            if (cs)
            {
                m_KernelReduce = cs.FindKernel("FfxParallelSortReduce");
                m_KernelScanAdd = cs.FindKernel("FfxParallelSortScanAdd");
                m_KernelScan = cs.FindKernel("FfxParallelSortScan");
                m_KernelScatter = cs.FindKernel("FfxParallelSortScatter");
                m_KernelSum = cs.FindKernel("FfxParallelSortCount");
            }

            m_Valid = m_KernelReduce >= 0 &&
                      m_KernelScanAdd >= 0 &&
                      m_KernelScan >= 0 &&
                      m_KernelScatter >= 0 &&
                      m_KernelSum >= 0;
            if (m_Valid)
            {
                if (!cs.IsSupported(m_KernelReduce) ||
                    !cs.IsSupported(m_KernelScanAdd) ||
                    !cs.IsSupported(m_KernelScan) ||
                    !cs.IsSupported(m_KernelScatter) ||
                    !cs.IsSupported(m_KernelSum))
                {
                    m_Valid = false;
                }
            }
        }

        static uint DivRoundUp(uint x, uint y) => (x + y - 1) / y;

        struct SortConstants
        {
            public uint numKeys;                              // The number of keys to sort
            public uint numBlocksPerThreadGroup;              // How many blocks of keys each thread group needs to process
            public uint numThreadGroups;                      // How many thread groups are being run concurrently for sort
            public uint numThreadGroupsWithAdditionalBlocks;  // How many thread groups need to process additional block data
            public uint numReduceThreadgroupPerBin;           // How many thread groups are summed together for each reduced bin entry
            public uint numScanValues;                        // How many values to perform scan prefix (+ add) on
            public uint shift;                                // What bits are being sorted (4 bit increments)
            public uint padding;                              // Padding - unused
        }

        public void Dispatch(CommandBuffer cmd, Args args)
        {
            Assert.IsTrue(Valid);

            GraphicsBuffer srcKeyBuffer = args.inputKeys;
            GraphicsBuffer srcPayloadBuffer = args.inputValues;
            GraphicsBuffer dstKeyBuffer = args.resources.sortScratchBuffer;
            GraphicsBuffer dstPayloadBuffer = args.resources.payloadScratchBuffer;

            // Initialize constants for the sort job
            SortConstants constants = default;
            constants.numKeys = args.count;

            uint BlockSize = FFX_PARALLELSORT_ELEMENTS_PER_THREAD * FFX_PARALLELSORT_THREADGROUP_SIZE;
            uint NumBlocks = DivRoundUp(args.count, BlockSize);

            // Figure out data distribution
            uint numThreadGroupsToRun = FFX_PARALLELSORT_MAX_THREADGROUPS_TO_RUN;
            uint BlocksPerThreadGroup = (NumBlocks / numThreadGroupsToRun);
            constants.numThreadGroupsWithAdditionalBlocks = NumBlocks % numThreadGroupsToRun;

            if (NumBlocks < numThreadGroupsToRun)
            {
                BlocksPerThreadGroup = 1;
                numThreadGroupsToRun = NumBlocks;
                constants.numThreadGroupsWithAdditionalBlocks = 0;
            }

            constants.numThreadGroups = numThreadGroupsToRun;
            constants.numBlocksPerThreadGroup = BlocksPerThreadGroup;

            // Calculate the number of thread groups to run for reduction (each thread group can process BlockSize number of entries)
            uint numReducedThreadGroupsToRun = FFX_PARALLELSORT_SORT_BIN_COUNT * ((BlockSize > numThreadGroupsToRun) ? 1 : (numThreadGroupsToRun + BlockSize - 1) / BlockSize);
            constants.numReduceThreadgroupPerBin = numReducedThreadGroupsToRun / FFX_PARALLELSORT_SORT_BIN_COUNT;
            constants.numScanValues = numReducedThreadGroupsToRun;	// The number of reduce thread groups becomes our scan count (as each thread group writes out 1 value that needs scan prefix)

            // Setup overall constants
            cmd.SetComputeIntParam(m_CS, "numKeys", (int)constants.numKeys);
            cmd.SetComputeIntParam(m_CS, "numBlocksPerThreadGroup", (int)constants.numBlocksPerThreadGroup);
            cmd.SetComputeIntParam(m_CS, "numThreadGroups", (int)constants.numThreadGroups);
            cmd.SetComputeIntParam(m_CS, "numThreadGroupsWithAdditionalBlocks", (int)constants.numThreadGroupsWithAdditionalBlocks);
            cmd.SetComputeIntParam(m_CS, "numReduceThreadgroupPerBin", (int)constants.numReduceThreadgroupPerBin);
            cmd.SetComputeIntParam(m_CS, "numScanValues", (int)constants.numScanValues);

            // Execute the sort algorithm in 4-bit increments
            constants.shift = 0;
            for (uint i = 0; constants.shift < 32; constants.shift += FFX_PARALLELSORT_SORT_BITS_PER_PASS, ++i)
            {
                cmd.SetComputeIntParam(m_CS, "shift", (int)constants.shift);

                // Sum
                cmd.SetComputeBufferParam(m_CS, m_KernelSum, "rw_source_keys", srcKeyBuffer);
                cmd.SetComputeBufferParam(m_CS, m_KernelSum, "rw_sum_table", args.resources.scratchBuffer);
                cmd.DispatchCompute(m_CS, m_KernelSum, (int)numThreadGroupsToRun, 1, 1);

                // Reduce
                cmd.SetComputeBufferParam(m_CS, m_KernelReduce, "rw_sum_table", args.resources.scratchBuffer);
                cmd.SetComputeBufferParam(m_CS, m_KernelReduce, "rw_reduce_table", args.resources.reducedScratchBuffer);
                cmd.DispatchCompute(m_CS, m_KernelReduce, (int)numReducedThreadGroupsToRun, 1, 1);

                // Scan
                cmd.SetComputeBufferParam(m_CS, m_KernelScan, "rw_scan_source", args.resources.reducedScratchBuffer);
                cmd.SetComputeBufferParam(m_CS, m_KernelScan, "rw_scan_dest", args.resources.reducedScratchBuffer);
                cmd.DispatchCompute(m_CS, m_KernelScan, 1, 1, 1);

                // Scan add
                cmd.SetComputeBufferParam(m_CS, m_KernelScanAdd, "rw_scan_source", args.resources.scratchBuffer);
                cmd.SetComputeBufferParam(m_CS, m_KernelScanAdd, "rw_scan_dest", args.resources.scratchBuffer);
                cmd.SetComputeBufferParam(m_CS, m_KernelScanAdd, "rw_scan_scratch", args.resources.reducedScratchBuffer);
                cmd.DispatchCompute(m_CS, m_KernelScanAdd, (int)numReducedThreadGroupsToRun, 1, 1);

                // Scatter
                cmd.SetComputeBufferParam(m_CS, m_KernelScatter, "rw_source_keys", srcKeyBuffer);
                cmd.SetComputeBufferParam(m_CS, m_KernelScatter, "rw_dest_keys", dstKeyBuffer);
                cmd.SetComputeBufferParam(m_CS, m_KernelScatter, "rw_sum_table", args.resources.scratchBuffer);
                cmd.SetComputeBufferParam(m_CS, m_KernelScatter, "rw_source_payloads", srcPayloadBuffer);
                cmd.SetComputeBufferParam(m_CS, m_KernelScatter, "rw_dest_payloads", dstPayloadBuffer);
                cmd.DispatchCompute(m_CS, m_KernelScatter, (int)numThreadGroupsToRun, 1, 1);

                // Swap
                (srcKeyBuffer, dstKeyBuffer) = (dstKeyBuffer, srcKeyBuffer);
                (srcPayloadBuffer, dstPayloadBuffer) = (dstPayloadBuffer, srcPayloadBuffer);
            }
        }
    }
}
