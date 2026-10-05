// SPDX-License-Identifier: MIT

using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Gaussians.Core;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Gaussians.ThreeD
{
    [ExecuteInEditMode]
    [AddComponentMenu("Gaussians/3D Splat Renderer")]
    public partial class GaussianSplat3DRenderer : MonoBehaviour
    {
        public enum RenderMode
        {
            Splats,
            DebugPoints,
            DebugPointIndices,
            DebugBoxes,
            DebugChunkBounds,
        }

        public enum RenderPath
        {
            CompositeTexture,
            DirectTransparent,
        }

        public GaussianSplat3DAsset m_Asset;

        [Tooltip("Composite Texture preserves the original front-to-back offscreen accumulation. Direct Transparent sorts back-to-front and submits premultiplied splats to Unity's transparent queue.")]
        public RenderPath m_RenderPath = RenderPath.DirectTransparent;
        [Tooltip("Rendering order within the selected render path. Higher values render later/on top. When paths are mixed, the Composite Texture group is always resolved before the Direct Transparent group.")]
        public int m_RenderOrder;
        [Tooltip("Direct Transparent: convert gamma-encoded colors before blending into a Linear project target. Leave off for assets already trained/exported in linear space.")]
        public bool m_ConvertGammaToLinear = true;
        [Tooltip("URP only: adds a draw writing approximate splat-center depth after transparent color for XR reprojection. Does not change color blending or fill empty background pixels.")]
        public bool m_WriteDepth = false;
        // One 8-bit alpha step. This is a coverage and
        // quality/performance convention, not a scene-specific depth criterion.
        public const float DefaultAlphaCutoff = 1.0f / 255.0f;
        [Range(0.0f, 1.0f)]
        [Tooltip("Advanced: minimum per-splat fragment opacity retained by BOTH color and depth. Default is 1/255. Higher values remove faint contributions and can reduce blending work, but may thin surfaces or cause popping. This is not accumulated opacity. Zero retains all positive alpha within the existing splat geometry.")]
        public float m_AlphaCutoff = DefaultAlphaCutoff;
        [Tooltip("Shrink splat quads to the current opacity cutoff without changing the Gaussian falloff. Disable to compare against the original bounds. Applies to color and depth; selected splats retain their full footprint.")]
        [HideInInspector] public bool m_OpacityAwareBounds = true;
        [Range(0.1f, 2.0f)] [Tooltip("Additional scaling factor for the splats")]
        public float m_SplatScale = 1.0f;
        [Range(0.05f, 20.0f)]
        [Tooltip("Additional scaling factor for opacity")]
        public float m_OpacityScale = 1.0f;
        [Range(0, 3)] [Tooltip("Spherical Harmonics order to use")]
        public int m_SHOrder = 3;
        [Tooltip("Show only Spherical Harmonics contribution, using gray color")]
        public bool m_SHOnly;
        [Range(1,30)] [Tooltip("Sort splats only every N frames")]
        public int m_SortNthFrame = 1;
        [Tooltip("Compact surviving splat IDs on the GPU, then sort and draw only that population. View changes force a fresh compacted sort regardless of Sort Nth Frame.")]
        [HideInInspector] public bool m_CompactVisibleSplats;
        [Tooltip("Enable the optional size, distance and opacity rejection thresholds. Disabling preserves their configured values; mandatory near-plane and invalid-data guards remain.")]
        [HideInInspector] public bool m_EarlyRejection = true;
        [Tooltip("Load SH only after visibility checks. Disable to measure the cost of loading SH before rejection.")]
        [HideInInspector] public bool m_DeferredSHLoading = true;
        [Tooltip("Reject offscreen splats using conservative footprint bounds before covariance projection. Independent of threshold pruning.")]
        [HideInInspector] public bool m_EarlyFrustumCulling = true;
        [Min(-1), Tooltip("Rejected splats required to enable compaction and deferred SH. -1 disables, 0 always enables. Positive values use a recent asynchronous visibility estimate; 100,000 is an experimental default.")]
        public int m_CompactionThreshold = 100000;
        // Explicit benchmark overrides bypass the automatic bundle; not normal inspector controls.
        [HideInInspector] public bool m_OptimizationOverrides;
        float MinimumOpacity => EffectiveOptimizationOverrides ? (m_EarlyRejection ? Mathf.Clamp01(m_MinimumSplatOpacity) : 0) :
            Mathf.Max(0, EffectiveAlphaCutoff * 0.998f - 5.96046448e-8f);
        public enum SortPrecision { Bits32 = 32, Bits24 = 24, Bits16 = 16 }
        [Tooltip("Depth-key precision: 32 retains full precision; 24/16 discard low mantissa bits and use three/two radix passes. Lower precision can cause ordering artifacts.")]
        public SortPrecision m_SortPrecision = SortPrecision.Bits32;
        internal int sortKeyBits => EffectiveSortPrecision == SortPrecision.Bits16 ? 16 : EffectiveSortPrecision == SortPrecision.Bits24 ? 24 : 32;
        [Min(0)] [Tooltip("Minimum camera-space center depth in world units while Early Rejection is enabled. Combined with the camera near plane using the larger distance. Default 0.1 protects against very close splats; zero uses only the camera near plane.")]
        public float m_MinimumSplatDistance = 0.1f;
        [Range(0, 1)] [Tooltip("Reject splats below this peak opacity after opacity scaling, before SH loading. Zero disables additional opacity rejection. Selected splats are exempt.")]
        [HideInInspector] public float m_MinimumSplatOpacity;
        [Min(0.0f)]
        [Tooltip("Cull splats below this projected three-sigma pixel radius, before covariance filtering. Zero preserves existing filtering. Higher values can remove detail or cause popping.")]
        public float m_MinimumSplatRadiusPixels = 0.7f;

        public RenderMode m_RenderMode = RenderMode.Splats;
        [Range(1.0f,15.0f)] public float m_PointDisplaySize = 3.0f;

        public GaussianCutout[] m_Cutouts;

        public Shader m_ShaderSplats;
        public Shader m_ShaderComposite;
        public Shader m_ShaderDebugPoints;
        public Shader m_ShaderDebugBoxes;
        [Tooltip("3D Gaussian splatting compute shader")]
        public ComputeShader m_CSSplatUtilities;

        int m_SplatCount; // initially same as asset splat count, but editing can change this
        GraphicsBuffer m_GpuPosData;
        GraphicsBuffer m_GpuOtherData;
        GraphicsBuffer m_GpuSHData;
        Texture m_GpuColorData;
        internal GraphicsBuffer m_GpuChunks;
        internal bool m_GpuChunksValid;
        internal GraphicsBuffer m_GpuIndexBuffer;

        // these buffers are only for splat editing, and are lazily created
        readonly GaussianCutoutBuffer m_CutoutBuffer = new();
        GraphicsBuffer m_GpuEditCountsBounds;
        GraphicsBuffer m_GpuEditSelected;
        GraphicsBuffer m_GpuEditDeleted;
        GraphicsBuffer m_GpuEditSelectedMouseDown; // selection state at start of operation
        GraphicsBuffer m_GpuEditPosMouseDown; // position state at start of operation
        GraphicsBuffer m_GpuEditOtherMouseDown; // rotation/scale state at start of operation

        GpuSorting m_Sorter;

        internal Material m_MatSplats;
        internal Material m_MatSplatsDirect;
        internal Material m_MatComposite;
        internal Material m_MatCompositeRaw;
        internal Material GetCompositeMaterial(bool convert) => convert ? m_MatComposite : m_MatCompositeRaw;
        internal Material m_MatDebugPoints;
        internal Material m_MatDebugBoxes;
        Mesh m_DirectQuadMesh;

        // Explicitly scoped to the separate benchmark diagnostic replay. No counter buffer
        // or instrumented shader is used by normal rendering / timing trials.
        Camera m_BenchmarkCamera;
        bool m_BenchmarkCaptureFrame;
        GraphicsBuffer m_BenchmarkCounts;
        int m_BenchmarkCountFrame = -1;
        // Diagnostic CSVs reserve slot 12 so counter columns keep stable indices.
        static readonly uint[] s_EmptyBenchmarkCounts = new uint[13];

        public void BeginBenchmarkDiagnostics(Camera camera)
        {
            if (!camera || camera.stereoEnabled) throw new ArgumentException("Diagnostics require a mono camera.");
            EndBenchmarkDiagnostics();
            m_BenchmarkCamera = camera;
            m_BenchmarkCounts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 13, sizeof(uint));
            m_BenchmarkCounts.SetData(s_EmptyBenchmarkCounts);
        }

        public void SetBenchmarkCaptureFrame(bool capture)
        {
            m_BenchmarkCaptureFrame = capture;
            m_BenchmarkCountFrame = -1;
        }

        public void EndBenchmarkDiagnostics()
        {
            m_BenchmarkCamera = null;
            m_BenchmarkCaptureFrame = false;
            m_BenchmarkCounts?.Dispose();
            m_BenchmarkCounts = null;
            m_BenchmarkCountFrame = -1;
        }

        static ulong SumBenchmarkOutcomes(uint[] counts)
        {
            ulong sum = 0;
            for (int i = 1; i < counts.Length; i++) sum += counts[i];
            return sum;
        }

        public uint[] ReadBenchmarkDiagnostics(out uint sortPopulation, out uint submittedInstances)
        {
            if (m_BenchmarkCounts == null || m_BenchmarkCountFrame != Time.frameCount)
                throw new InvalidOperationException("No current-frame diagnostic dispatch for this renderer/camera.");
            var counts = new uint[13];
            m_BenchmarkCounts.GetData(counts); // Deliberately blocking; diagnostic pass only.
            if (counts[0] != m_SplatCount || SumBenchmarkOutcomes(counts) != counts[0])
                throw new InvalidOperationException("Diagnostic input/rejection counts are inconsistent.");
            sortPopulation = submittedInstances = (uint)m_SplatCount;
            if (m_CameraRenderResources[m_BenchmarkCamera].Compacted)
            {
                var resources = m_CameraRenderResources[m_BenchmarkCamera];
                var args = new uint[10];
                resources.CompactArgs.GetData(args);
                sortPopulation = args[0];
                submittedInstances = args[6];
            }
            return counts;
        }

        GaussianSplat3DAsset m_PrevAsset;
        Hash128 m_PrevHash;
        bool m_Registered;
        bool m_PreviousDirectTransparentPath;
        int m_RenderDataVersion;

        internal static class Props
        {
            public static readonly int ConvertGammaToLinear = Shader.PropertyToID("_ConvertGammaToLinear");
            public static readonly int AlphaCutoff = Shader.PropertyToID("_AlphaCutoff");
            public static readonly int OpacityAwareBounds = Shader.PropertyToID("_OpacityAwareBounds");
            public static readonly int SplatPos = Shader.PropertyToID("_SplatPos");
            public static readonly int SplatOther = Shader.PropertyToID("_SplatOther");
            public static readonly int SplatSH = Shader.PropertyToID("_SplatSH");
            public static readonly int SplatColor = Shader.PropertyToID("_SplatColor");
            public static readonly int SplatSelectedBits = Shader.PropertyToID("_SplatSelectedBits");
            public static readonly int SplatDeletedBits = Shader.PropertyToID("_SplatDeletedBits");
            public static readonly int SplatBitsValid = Shader.PropertyToID("_SplatBitsValid");
            public static readonly int SplatFormat = Shader.PropertyToID("_SplatFormat");
            public static readonly int SplatChunks = Shader.PropertyToID("_SplatChunks");
            public static readonly int SplatChunkCount = Shader.PropertyToID("_SplatChunkCount");
            public static readonly int SplatViewData = Shader.PropertyToID("_SplatViewData");
            public static readonly int OrderBuffer = Shader.PropertyToID("_OrderBuffer");
            public static readonly int SplatScale = Shader.PropertyToID("_SplatScale");
            public static readonly int SplatOpacityScale = Shader.PropertyToID("_SplatOpacityScale");
            public static readonly int SplatSize = Shader.PropertyToID("_SplatSize");
            public static readonly int SplatCount = Shader.PropertyToID("_SplatCount");
            public static readonly int SHOrder = Shader.PropertyToID("_SHOrder");
            public static readonly int SHOnly = Shader.PropertyToID("_SHOnly");
            public static readonly int DisplayIndex = Shader.PropertyToID("_DisplayIndex");
            public static readonly int DisplayChunks = Shader.PropertyToID("_DisplayChunks");
            public static readonly int GaussianSplatRT = Shader.PropertyToID("_GaussianSplatRT");
            public static readonly int SplatSortKeys = Shader.PropertyToID("_SplatSortKeys");
            public static readonly int SplatSortDistances = Shader.PropertyToID("_SplatSortDistances");
            public static readonly int SrcBuffer = Shader.PropertyToID("_SrcBuffer");
            public static readonly int DstBuffer = Shader.PropertyToID("_DstBuffer");
            public static readonly int BufferSize = Shader.PropertyToID("_BufferSize");
            public static readonly int ProjectionMatrix = Shader.PropertyToID("_ProjectionMatrix");
            public static readonly int SplatViewCount = Shader.PropertyToID("_SplatViewCount");
            public static readonly int SplatEyeIndex = Shader.PropertyToID("_SplatEyeIndex");
            public static readonly int MatrixMV = Shader.PropertyToID("_MatrixMV");
            public static readonly int MatrixMVP = Shader.PropertyToID("_MatrixMVP");
            public static readonly int MatrixObjectToWorld = Shader.PropertyToID("_MatrixObjectToWorld");
            public static readonly int MatrixWorldToObject = Shader.PropertyToID("_MatrixWorldToObject");
            public static readonly int VecScreenParams = Shader.PropertyToID("_VecScreenParams");
            public static readonly int VecWorldSpaceCameraPos = Shader.PropertyToID("_VecWorldSpaceCameraPos");
            public static readonly int MinimumSplatRadiusPixels = Shader.PropertyToID("_MinimumSplatRadiusPixels");
            public static readonly int SortDescending = Shader.PropertyToID("_SortDescending");
            public static readonly int SrcBlend = Shader.PropertyToID("_SrcBlend");
            public static readonly int DstBlend = Shader.PropertyToID("_DstBlend");
            public static readonly int CameraTargetTexture = Shader.PropertyToID("_CameraTargetTexture");
            public static readonly int SelectionCenter = Shader.PropertyToID("_SelectionCenter");
            public static readonly int SelectionDelta = Shader.PropertyToID("_SelectionDelta");
            public static readonly int SelectionDeltaRot = Shader.PropertyToID("_SelectionDeltaRot");
            public static readonly int SplatCutoutsCount = Shader.PropertyToID("_SplatCutoutsCount");
            public static readonly int SplatCutouts = Shader.PropertyToID("_SplatCutouts");
            public static readonly int SelectionMode = Shader.PropertyToID("_SelectionMode");
            public static readonly int SplatPosMouseDown = Shader.PropertyToID("_SplatPosMouseDown");
            public static readonly int SplatOtherMouseDown = Shader.PropertyToID("_SplatOtherMouseDown");
        }

        [field: NonSerialized] public bool editModified { get; private set; }
        [field: NonSerialized] public uint editSelectedSplats { get; private set; }
        [field: NonSerialized] public uint editDeletedSplats { get; private set; }
        [field: NonSerialized] public uint editCutSplats { get; private set; }
        [field: NonSerialized] public Bounds editSelectedBounds { get; private set; }

        public GaussianSplat3DAsset asset => m_Asset;
        public int splatCount => m_SplatCount;
        bool effectiveDirectConversion => usesDirectTransparentPath && EffectiveConvertGammaToLinear && QualitySettings.activeColorSpace == ColorSpace.Linear;
        internal bool usesDirectTransparentPath =>
            EffectiveRenderPath == RenderPath.DirectTransparent && EffectiveRenderMode == RenderMode.Splats;

        enum KernelIndices
        {
            SetIndices,
            CalcDistances,
            CalcViewData,
            UpdateEditData,
            InitEditData,
            ClearBuffer,
            InvertSelection,
            SelectAll,
            OrBuffers,
            SelectionUpdate,
            TranslateSelection,
            RotateSelection,
            ScaleSelection,
            ExportData,
            CopySplats,
        }

        public bool HasValidAsset =>
            m_Asset != null &&
            m_Asset.splatCount > 0 &&
            m_Asset.formatVersion == GaussianSplat3DAsset.kCurrentVersion &&
            (m_Asset.hasValidFloatData || (m_Asset.posData != null &&
            m_Asset.otherData != null &&
            m_Asset.shData != null &&
            m_Asset.colorData != null));
        public bool HasValidRenderSetup => m_GpuPosData != null && m_GpuOtherData != null && m_GpuChunks != null;

        void CreateResourcesForAsset()
        {
            if (!HasValidAsset)
                return;

            m_SplatCount = asset.splatCount;
            m_FloatData = new GaussianSplat3DData(asset.isFloatSource ? m_SplatCount : 1,
                asset.isFloatSource ? (asset.floatSHDegree + 1) * (asset.floatSHDegree + 1) : 1, asset.floatSHStorage);
            if (asset.isFloatSource)
            {
                m_FloatData.Splats.SetData(asset.floatSplats.GetData<Vector4>());
                m_FloatData.SH.SetData(asset.floatSH.GetData<uint>());
                // Bind valid dummies for the packed branch; no packed copy of the cloud.
                m_GpuPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, 4, 4);
                m_GpuOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, 4, 4);
                m_GpuSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, 48, 4);
                m_GpuColorData = Texture2D.blackTexture;
            }
            else
            {
                m_GpuPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int) (asset.posData.dataSize / 4), 4) { name = "GaussianPosData" };
                m_GpuPosData.SetData(asset.posData.GetData<uint>());
                m_GpuOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int) (asset.otherData.dataSize / 4), 4) { name = "GaussianOtherData" };
                m_GpuOtherData.SetData(asset.otherData.GetData<uint>());
                m_GpuSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, (int) (asset.shData.dataSize / 4), 4) { name = "GaussianSHData" };
                m_GpuSHData.SetData(asset.shData.GetData<uint>());
                var (texWidth, texHeight) = GaussianSplat3DAsset.CalcTextureSize(asset.splatCount);
                var texFormat = GaussianSplat3DAsset.ColorFormatToGraphics(asset.colorFormat);
                var tex = new Texture2D(texWidth, texHeight, texFormat, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.DontUploadUponCreate) { name = "GaussianColorData" };
                tex.SetPixelData(asset.colorData.GetData<byte>(), 0);
                tex.Apply(false, true);
                m_GpuColorData = tex;
            }
            if (asset.chunkData != null && asset.chunkData.dataSize != 0)
            {
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    (int) (asset.chunkData.dataSize / UnsafeUtility.SizeOf<GaussianSplat3DAsset.ChunkInfo>()),
                    UnsafeUtility.SizeOf<GaussianSplat3DAsset.ChunkInfo>()) {name = "GaussianChunkData"};
                m_GpuChunks.SetData(asset.chunkData.GetData<GaussianSplat3DAsset.ChunkInfo>());
                m_GpuChunksValid = true;
            }
            else
            {
                // Bind a valid buffer even when the unchunked shader branch does not read it.
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1,
                    UnsafeUtility.SizeOf<GaussianSplat3DAsset.ChunkInfo>()) {name = "GaussianChunkData"};
                m_GpuChunksValid = false;
            }

            m_GpuIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 36, 2);
            // cube indices, most often we use only the first quad
            m_GpuIndexBuffer.SetData(new ushort[]
            {
                0, 1, 2, 1, 3, 2,
                4, 6, 5, 5, 6, 7,
                0, 2, 4, 4, 2, 6,
                1, 5, 3, 5, 7, 3,
                0, 4, 1, 4, 5, 1,
                2, 3, 6, 3, 7, 6
            });
        }

        bool resourcesAreSetUp => EffectiveShaderSplats != null && EffectiveShaderComposite != null && m_ShaderDebugPoints != null &&
                                  m_ShaderDebugBoxes != null && EffectiveCSSplatUtilities != null && SystemInfo.supportsComputeShaders;

        public void EnsureMaterials()
        {
            if (m_MatSplats && (m_MatSplats.shader != EffectiveShaderSplats || m_MatComposite.shader != EffectiveShaderComposite))
            {
                DestroyImmediate(m_MatSplats); DestroyImmediate(m_MatSplatsDirect);
                DestroyImmediate(m_MatComposite); DestroyImmediate(m_MatCompositeRaw);
                DestroyImmediate(m_MatDebugPoints); DestroyImmediate(m_MatDebugBoxes);
                DisposeCameraRenderResources();
                InvalidateCameraPreparation();
            }
            if (m_MatSplats == null && resourcesAreSetUp)
            {
                m_MatSplats = new Material(EffectiveShaderSplats)
                {
                    name = "GaussianSplats3D",
                    enableInstancing = true,
                };
                m_MatSplats.SetInt(Props.SrcBlend, (int)BlendMode.OneMinusDstAlpha);
                m_MatSplats.SetInt(Props.DstBlend, (int)BlendMode.One);

                m_MatSplatsDirect = new Material(EffectiveShaderSplats)
                {
                    name = "GaussianSplats3DDirectTransparent",
                    enableInstancing = true,
                };
                m_MatSplatsDirect.SetInt(Props.SrcBlend, (int)BlendMode.One);
                m_MatSplatsDirect.SetInt(Props.DstBlend, (int)BlendMode.OneMinusSrcAlpha);
                m_MatSplatsDirect.EnableKeyword("GAUSSIANS_DIRECT_TRANSPARENT");
                m_MatComposite = new Material(EffectiveShaderComposite)
                {
                    name = "GaussianSplat3DComposite",
                    enableInstancing = true,
                };
                m_MatComposite.SetInteger("_ConvertCompositeGammaToLinear", 1);
                m_MatCompositeRaw = new Material(m_MatComposite);
                m_MatCompositeRaw.SetInteger("_ConvertCompositeGammaToLinear", 0);
                m_MatDebugPoints = new Material(m_ShaderDebugPoints) {name = "GaussianSplat3DDebugPoints", enableInstancing = true};
                m_MatDebugBoxes = new Material(m_ShaderDebugBoxes) {name = "GaussianSplat3DDebugBoxes", enableInstancing = true};
            }
        }

        void EnsureDirectQuadMesh()
        {
            if (m_DirectQuadMesh != null)
                return;

            m_DirectQuadMesh = new Mesh
            {
                name = "GaussianSplat3DDirectQuad",
                hideFlags = HideFlags.HideAndDontSave,
            };
            m_DirectQuadMesh.vertices = new[]
            {
                Vector3.zero,
                Vector3.zero,
                Vector3.zero,
                Vector3.zero,
            };
            m_DirectQuadMesh.SetIndices(new[] {0, 1, 2, 1, 3, 2}, MeshTopology.Triangles, 0, false);
            m_DirectQuadMesh.UploadMeshData(true);
        }

        ComputeShader m_SorterShader;
        public void EnsureSorterAndRegister()
        {
            if ((m_Sorter == null || m_SorterShader != EffectiveCSSplatUtilities) && resourcesAreSetUp)
            {
                m_SorterShader = EffectiveCSSplatUtilities;
                m_Sorter = new GpuSorting(m_SorterShader);
            }

            if (!m_Registered && resourcesAreSetUp)
            {
                GaussianSplat3DRenderSystem.instance.RegisterSplat(this);
                m_Registered = true;
            }
        }

        public void OnEnable()
        {
            ResolveShaders();
#if UNITY_EDITOR
            UnityEditor.Undo.undoRedoPerformed += OnUndoRedo;
#endif
            InvalidateCameraPreparation();
            m_PreviousDirectTransparentPath = usesDirectTransparentPath;
            if (!resourcesAreSetUp)
                return;

            EnsureMaterials();
            EnsureSorterAndRegister();

            CreateResourcesForAsset();
            // The first Update must not rebuild resources just initialized by OnEnable.
            m_PrevAsset = m_Asset;
            m_PrevHash = m_Asset ? m_Asset.dataHash : new Hash128();
        }

        void SetAssetDataOnCS(CommandBuffer cmb, KernelIndices kernel, CameraRenderResources cameraResources = null)
            => SetAssetDataOnCS(cmb, (int)kernel, cameraResources);

        void SetAssetDataOnCS(CommandBuffer cmb, int kernelIndex, CameraRenderResources cameraResources)
        {
            ComputeShader cs = EffectiveCSSplatUtilities;
            BindSource(cmb, cs, kernelIndex);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatPos, m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatChunks, m_GpuChunks);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatOther, m_GpuOtherData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSH, m_GpuSHData);
            cmb.SetComputeTextureParam(cs, kernelIndex, Props.SplatColor, m_GpuColorData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSelectedBits, m_GpuEditSelected ?? m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatDeletedBits, m_GpuEditDeleted ?? m_GpuPosData);
            if (cameraResources != null)
            {
                cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatViewData, cameraResources.GpuView);
                cmb.SetComputeBufferParam(cs, kernelIndex, Props.OrderBuffer, cameraResources.GpuSortKeys);
            }

            cmb.SetComputeIntParam(cs, Props.SplatBitsValid, m_GpuEditSelected != null && m_GpuEditDeleted != null ? 1 : 0);
            uint format = (uint)m_Asset.posFormat | ((uint)m_Asset.scaleFormat << 8) | ((uint)m_Asset.shFormat << 16);
            cmb.SetComputeIntParam(cs, Props.SplatFormat, (int)format);
            cmb.SetComputeIntParam(cs, Props.SplatCount, m_SplatCount);
            cmb.SetComputeIntParam(cs, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);

            RefreshCutouts(transform.localToWorldMatrix);
            m_CutoutBuffer.EnsureUploaded();
            cmb.SetComputeIntParam(cs, Props.SplatCutoutsCount, m_CutoutBuffer.Count);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatCutouts, m_CutoutBuffer.Buffer);
        }

        internal void SetAssetDataOnMaterial(MaterialPropertyBlock mat)
        {
            BindSource(mat);
            mat.SetFloat(Props.AlphaCutoff, EffectiveAlphaCutoff);
            mat.SetInt(Props.OpacityAwareBounds, EffectiveOpacityAwareBounds ? 1 : 0);
            mat.SetInt(Props.ConvertGammaToLinear, effectiveDirectConversion ? 1 : 0);
            mat.SetFloat(Props.MinimumSplatRadiusPixels, (m_EarlyRejection ? Mathf.Max(0, m_MinimumSplatRadiusPixels) : 0));
            mat.SetInt(Props.SortDescending, usesDirectTransparentPath ? 1 : 0);
            mat.SetMatrix(Props.MatrixObjectToWorld, transform.localToWorldMatrix);
            mat.SetBuffer(Props.SplatPos, m_GpuPosData);
            mat.SetBuffer(Props.SplatOther, m_GpuOtherData);
            mat.SetBuffer(Props.SplatSH, m_GpuSHData);
            mat.SetTexture(Props.SplatColor, m_GpuColorData);
            mat.SetBuffer(Props.SplatSelectedBits, m_GpuEditSelected ?? m_GpuPosData);
            mat.SetBuffer(Props.SplatDeletedBits, m_GpuEditDeleted ?? m_GpuPosData);
            mat.SetInt(Props.SplatBitsValid, m_GpuEditSelected != null && m_GpuEditDeleted != null ? 1 : 0);
            uint format = (uint)m_Asset.posFormat | ((uint)m_Asset.scaleFormat << 8) | ((uint)m_Asset.shFormat << 16);
            mat.SetInteger(Props.SplatFormat, (int)format);
            mat.SetInteger(Props.SplatCount, m_SplatCount);
            mat.SetInteger(Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
        }

        internal void BindViewProperties(MaterialPropertyBlock properties, Camera camera, CameraRenderResources resources)
        {
            SetAssetDataOnMaterial(properties);
            properties.SetBuffer(Props.SplatChunks, m_GpuChunks);
            properties.SetBuffer(Props.SplatViewData, resources.GpuView);
            properties.SetBuffer(Props.OrderBuffer, resources.GpuSortKeys);
            properties.SetFloat(Props.SplatScale, m_SplatScale);
            properties.SetFloat(Props.SplatOpacityScale, m_OpacityScale);
            properties.SetInteger(Props.SHOrder, m_SHOrder);
            properties.SetInteger(Props.SHOnly, m_SHOnly ? 1 : 0);
            SetCameraProperties(properties, camera, resources);
        }

        void UpdateDirectMaterialProperties(Camera cam, CameraRenderResources resources)
        {
            resources.DirectMaterialProperties.Clear();
            BindViewProperties(resources.DirectMaterialProperties, cam, resources);
        }

        internal void QueueDirectTransparentDraw(Camera cam)
        {
            if (cam == null || !usesDirectTransparentPath || !HasValidAsset || !HasValidRenderSetup)
                return;

            EnsureMaterials();
            EnsureDirectQuadMesh();
            CameraRenderResources cameraResources = GetCameraRenderResources(cam);
            if (m_MatSplatsDirect == null || m_DirectQuadMesh == null || cameraResources == null)
                return;

            UpdateDirectMaterialProperties(cam, cameraResources);
            cameraResources.DirectMaterial ??= new Material(m_MatSplatsDirect);
            cameraResources.DirectMaterial.renderQueue = (int)RenderQueue.Transparent + Mathf.Clamp(EffectiveRenderOrder, -499, 500);

            Bounds worldBounds = GetWorldBounds(cam);
            var renderParams = new RenderParams(cameraResources.DirectMaterial)
            {
                worldBounds = worldBounds,
                matProps = cameraResources.DirectMaterialProperties,
                layer = gameObject.layer,
                camera = cam,
            };
            if (UseCompaction(cameraResources))
                Graphics.DrawProceduralIndirect(cameraResources.DirectMaterial, worldBounds, MeshTopology.Triangles,
                    m_GpuIndexBuffer, GetIndirectArgs(cameraResources), 20, cam,
                    cameraResources.DirectMaterialProperties, ShadowCastingMode.Off, false, gameObject.layer);
            else
                Graphics.RenderMeshPrimitives(renderParams, m_DirectQuadMesh, 0, splatCount);
        }

        internal Bounds GetWorldBounds(Camera cam)
        {
            Vector3 boundsMin = asset.boundsIncludeSplatExtents ? asset.renderBoundsMin : asset.boundsMin;
            Vector3 boundsMax = asset.boundsIncludeSplatExtents ? asset.renderBoundsMax : asset.boundsMax;
            Bounds localBounds = new((boundsMin + boundsMax) * 0.5f, boundsMax - boundsMin);
            Bounds worldBounds = GaussianRenderMath.TransformBounds(transform.localToWorldMatrix, localBounds);
            if (m_SourceFrame.Data != null || !asset.boundsIncludeSplatExtents || editModified || Mathf.Abs(m_SplatScale) > 2.0f)
            {
                // Assets created before splat-extent bounds were introduced only contain
                // center bounds. Keep the original center (used by transparent sorting), but
                // expand symmetrically until the current camera is inside. This guarantees the
                // old asset is not frustum-culled; recreating it restores tight object culling.
                Vector3 cameraOffset = cam.transform.position - worldBounds.center;
                cameraOffset = new Vector3(Mathf.Abs(cameraOffset.x), Mathf.Abs(cameraOffset.y), Mathf.Abs(cameraOffset.z));
                worldBounds.extents = Vector3.Max(worldBounds.extents, cameraOffset + Vector3.one);
                Vector3 interior = cam.ViewportToWorldPoint(new Vector3(0.5f, 0.5f,
                    Mathf.Lerp(cam.nearClipPlane, cam.farClipPlane, 0.5f))) - worldBounds.center;
                worldBounds.extents = Vector3.Max(worldBounds.extents,
                    new Vector3(Mathf.Abs(interior.x), Mathf.Abs(interior.y), Mathf.Abs(interior.z)) + Vector3.one);
            }
            // Covariance filtering adds a pixel footprint beyond the baked ellipsoid.
            float pixelMargin = cam.orthographic ? cam.orthographicSize * 2 / Mathf.Max(1, cam.pixelHeight) :
                2 * (Vector3.Distance(cam.transform.position, worldBounds.center) + worldBounds.extents.magnitude) *
                Mathf.Tan(cam.fieldOfView * Mathf.Deg2Rad * 0.5f) / Mathf.Max(1, cam.pixelHeight);
            worldBounds.Expand(Mathf.Max(0, pixelMargin) * 8);
            return worldBounds;
        }

        static void DisposeBuffer(ref GraphicsBuffer buf)
        {
            buf?.Dispose();
            buf = null;
        }

        void DisposeResourcesForAsset()
        {
            if (m_GpuColorData != Texture2D.blackTexture) DestroyImmediate(m_GpuColorData);
            m_GpuColorData = null;
            m_FloatData?.Dispose(); m_FloatData = null;
            m_SourceFrame = default;

            DisposeBuffer(ref m_GpuPosData);
            DisposeBuffer(ref m_GpuOtherData);
            DisposeBuffer(ref m_GpuSHData);
            DisposeBuffer(ref m_GpuChunks);

            DisposeBuffer(ref m_GpuIndexBuffer);
            DisposeCameraRenderResources();

            DisposeBuffer(ref m_GpuEditSelectedMouseDown);
            DisposeBuffer(ref m_GpuEditPosMouseDown);
            DisposeBuffer(ref m_GpuEditOtherMouseDown);
            DisposeBuffer(ref m_GpuEditSelected);
            DisposeBuffer(ref m_GpuEditDeleted);
            DisposeBuffer(ref m_GpuEditCountsBounds);
            m_CutoutBuffer.Dispose();

            m_SplatCount = 0;
            m_GpuChunksValid = false;

            editSelectedSplats = 0;
            editDeletedSplats = 0;
            editCutSplats = 0;
            editModified = false;
            editSelectedBounds = default;
        }

        void OnUndoRedo() { ++m_RenderDataVersion; InvalidateCameraPreparation(); }

        public void OnDisable()
        {
            EndBenchmarkDiagnostics();
#if UNITY_EDITOR
            UnityEditor.Undo.undoRedoPerformed -= OnUndoRedo;
#endif
            InvalidateCameraPreparation();
            DisposeResourcesForAsset();
            GaussianSplat3DRenderSystem.instance.UnregisterSplat(this);
            m_Registered = false;
            m_Sorter = null;

            DestroyImmediate(m_MatSplats);
            DestroyImmediate(m_MatSplatsDirect);
            DestroyImmediate(m_MatComposite);
            DestroyImmediate(m_MatCompositeRaw);
            DestroyImmediate(m_MatDebugPoints);
            DestroyImmediate(m_MatDebugBoxes);
            DestroyImmediate(m_DirectQuadMesh);
        }

        public void Update()
        {
            ReleaseDestroyedCameraRenderResources();

            bool directTransparentPath = usesDirectTransparentPath;
            if (m_PreviousDirectTransparentPath != directTransparentPath)
            {
                // The two paths require opposite sort orders. Force a sort immediately after
                // switching, even when Sort Nth Frame is greater than one.
                InvalidateCameraPreparation();
                m_PreviousDirectTransparentPath = directTransparentPath;
            }

            var curHash = m_Asset ? m_Asset.dataHash : new Hash128();
            if (m_PrevAsset != m_Asset || m_PrevHash != curHash)
            {
                m_PrevAsset = m_Asset;
                m_PrevHash = curHash;
                if (resourcesAreSetUp)
                {
                    DisposeResourcesForAsset();
                    CreateResourcesForAsset();
                    ++m_RenderDataVersion;
                }
                else
                {
                    Debug.LogError($"{nameof(GaussianSplat3DRenderer)} component is not set up correctly (Resource references are missing), or platform does not support compute shaders");
                }
            }

        }

        public void ActivateCamera(int index)
        {
            Camera mainCam = Camera.main;
            if (!mainCam)
                return;
            if (!m_Asset || m_Asset.cameras == null)
                return;

            var selfTr = transform;
            var camTr = mainCam.transform;
            var prevParent = camTr.parent;
            var cam = m_Asset.cameras[index];
            camTr.parent = selfTr;
            camTr.localPosition = cam.pos;
            camTr.localRotation = Quaternion.LookRotation(cam.axisZ, cam.axisY);
            camTr.parent = prevParent;
            camTr.localScale = Vector3.one;
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(camTr);
#endif
        }

        void ClearGraphicsBuffer(GraphicsBuffer buf)
        {
            EffectiveCSSplatUtilities.SetBuffer((int)KernelIndices.ClearBuffer, Props.DstBuffer, buf);
            EffectiveCSSplatUtilities.SetInt(Props.BufferSize, buf.count);
            EffectiveCSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.ClearBuffer, out uint gsX, out _, out _);
            EffectiveCSSplatUtilities.Dispatch((int)KernelIndices.ClearBuffer, (int)((buf.count+gsX-1)/gsX), 1, 1);
        }

        void UnionGraphicsBuffers(GraphicsBuffer dst, GraphicsBuffer src)
        {
            EffectiveCSSplatUtilities.SetBuffer((int)KernelIndices.OrBuffers, Props.SrcBuffer, src);
            EffectiveCSSplatUtilities.SetBuffer((int)KernelIndices.OrBuffers, Props.DstBuffer, dst);
            EffectiveCSSplatUtilities.SetInt(Props.BufferSize, dst.count);
            EffectiveCSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.OrBuffers, out uint gsX, out _, out _);
            EffectiveCSSplatUtilities.Dispatch((int)KernelIndices.OrBuffers, (int)((dst.count+gsX-1)/gsX), 1, 1);
        }

        static float SortableUintToFloat(uint v)
        {
            uint mask = ((v >> 31) - 1) | 0x80000000u;
            return math.asfloat(v ^ mask);
        }

        public void UpdateEditCountsAndBounds()
        {
            ++m_RenderDataVersion;
            if (m_GpuEditSelected == null)
            {
                ++m_RenderDataVersion;
                editSelectedSplats = 0;
                editDeletedSplats = 0;
                editCutSplats = 0;
                editModified = false;
                editSelectedBounds = default;
                return;
            }

            EffectiveCSSplatUtilities.SetBuffer((int)KernelIndices.InitEditData, Props.DstBuffer, m_GpuEditCountsBounds);
            EffectiveCSSplatUtilities.Dispatch((int)KernelIndices.InitEditData, 1, 1, 1);

            using CommandBuffer cmb = new CommandBuffer();
            SetAssetDataOnCS(cmb, KernelIndices.UpdateEditData);
            cmb.SetComputeBufferParam(EffectiveCSSplatUtilities, (int)KernelIndices.UpdateEditData, Props.DstBuffer, m_GpuEditCountsBounds);
            cmb.SetComputeIntParam(EffectiveCSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            EffectiveCSSplatUtilities.GetKernelThreadGroupSizes((int)KernelIndices.UpdateEditData, out uint gsX, out _, out _);
            cmb.DispatchCompute(EffectiveCSSplatUtilities, (int)KernelIndices.UpdateEditData, (int)((m_GpuEditSelected.count+gsX-1)/gsX), 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);

            uint[] res = new uint[m_GpuEditCountsBounds.count];
            m_GpuEditCountsBounds.GetData(res);
            editSelectedSplats = res[0];
            editDeletedSplats = res[1];
            editCutSplats = res[2];
            Vector3 min = new Vector3(SortableUintToFloat(res[3]), SortableUintToFloat(res[4]), SortableUintToFloat(res[5]));
            Vector3 max = new Vector3(SortableUintToFloat(res[6]), SortableUintToFloat(res[7]), SortableUintToFloat(res[8]));
            Bounds bounds = default;
            bounds.SetMinMax(min, max);
            if (bounds.extents.sqrMagnitude < 0.01)
                bounds.extents = new Vector3(0.1f,0.1f,0.1f);
            editSelectedBounds = bounds;
        }

        bool EnsureEditingBuffers()
        {
            if (!CanEditSplats || !HasValidAsset || !HasValidRenderSetup)
                return false;

            if (m_GpuEditSelected == null)
            {
                ++m_RenderDataVersion;
                var target = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource |
                             GraphicsBuffer.Target.CopyDestination;
                var size = (m_SplatCount + 31) / 32;
                m_GpuEditSelected = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatSelected"};
                m_GpuEditSelectedMouseDown = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatSelectedInit"};
                m_GpuEditDeleted = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatDeleted"};
                m_GpuEditCountsBounds = new GraphicsBuffer(target, 3 + 6, 4) {name = "GaussianSplatEditData"}; // selected count, deleted bound, cut count, float3 min, float3 max
                ClearGraphicsBuffer(m_GpuEditSelected);
                ClearGraphicsBuffer(m_GpuEditSelectedMouseDown);
                ClearGraphicsBuffer(m_GpuEditDeleted);
            }
            return m_GpuEditSelected != null;
        }

        public void EditStoreSelectionMouseDown()
        {
            if (!EnsureEditingBuffers()) return;
            Graphics.CopyBuffer(m_GpuEditSelected, m_GpuEditSelectedMouseDown);
        }

        public void EditStorePosMouseDown()
        {
            if (!CanEditSplats) return;
            if (m_GpuEditPosMouseDown == null)
            {
                m_GpuEditPosMouseDown = new GraphicsBuffer(m_GpuPosData.target | GraphicsBuffer.Target.CopyDestination, m_GpuPosData.count, m_GpuPosData.stride) {name = "GaussianSplatEditPosMouseDown"};
            }
            Graphics.CopyBuffer(m_GpuPosData, m_GpuEditPosMouseDown);
        }
        public void EditStoreOtherMouseDown()
        {
            if (!CanEditSplats) return;
            if (m_GpuEditOtherMouseDown == null)
            {
                m_GpuEditOtherMouseDown = new GraphicsBuffer(m_GpuOtherData.target | GraphicsBuffer.Target.CopyDestination, m_GpuOtherData.count, m_GpuOtherData.stride) {name = "GaussianSplatEditOtherMouseDown"};
            }
            Graphics.CopyBuffer(m_GpuOtherData, m_GpuEditOtherMouseDown);
        }

        public void EditUpdateSelection(Vector2 rectMin, Vector2 rectMax, Camera cam, bool subtract)
        {
            if (!EnsureEditingBuffers()) return;

            Graphics.CopyBuffer(m_GpuEditSelectedMouseDown, m_GpuEditSelected);

            var tr = transform;
            Matrix4x4 matView = cam.worldToCameraMatrix;
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
            Vector4 screenPar = new Vector4(screenW, screenH, 0, 0);
            Vector4 camPos = cam.transform.position;

            using var cmb = new CommandBuffer { name = "SplatSelectionUpdate" };
            SetAssetDataOnCS(cmb, KernelIndices.SelectionUpdate);

            cmb.SetComputeMatrixParam(EffectiveCSSplatUtilities, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(EffectiveCSSplatUtilities, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(EffectiveCSSplatUtilities, Props.MatrixWorldToObject, matW2O);

            cmb.SetComputeVectorParam(EffectiveCSSplatUtilities, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(EffectiveCSSplatUtilities, Props.VecWorldSpaceCameraPos, camPos);

            cmb.SetComputeVectorParam(EffectiveCSSplatUtilities, "_SelectionRect", new Vector4(rectMin.x, rectMax.y, rectMax.x, rectMin.y));
            cmb.SetComputeIntParam(EffectiveCSSplatUtilities, Props.SelectionMode, subtract ? 0 : 1);

            DispatchUtilsAndExecute(cmb, KernelIndices.SelectionUpdate, m_SplatCount);
            UpdateEditCountsAndBounds();
        }

        public void EditTranslateSelection(Vector3 localSpacePosDelta)
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatTranslateSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.TranslateSelection);

            cmb.SetComputeVectorParam(EffectiveCSSplatUtilities, Props.SelectionDelta, localSpacePosDelta);

            DispatchUtilsAndExecute(cmb, KernelIndices.TranslateSelection, m_SplatCount);
            ++m_RenderDataVersion;
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        public void EditRotateSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Quaternion rotation)
        {
            if (!EnsureEditingBuffers()) return;
            if (m_GpuEditPosMouseDown == null || m_GpuEditOtherMouseDown == null) return; // should have captured initial state

            using var cmb = new CommandBuffer { name = "SplatRotateSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.RotateSelection);

            cmb.SetComputeBufferParam(EffectiveCSSplatUtilities, (int)KernelIndices.RotateSelection, Props.SplatPosMouseDown, m_GpuEditPosMouseDown);
            cmb.SetComputeBufferParam(EffectiveCSSplatUtilities, (int)KernelIndices.RotateSelection, Props.SplatOtherMouseDown, m_GpuEditOtherMouseDown);
            cmb.SetComputeVectorParam(EffectiveCSSplatUtilities, Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(EffectiveCSSplatUtilities, Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(EffectiveCSSplatUtilities, Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(EffectiveCSSplatUtilities, Props.SelectionDeltaRot, new Vector4(rotation.x, rotation.y, rotation.z, rotation.w));

            DispatchUtilsAndExecute(cmb, KernelIndices.RotateSelection, m_SplatCount);
            ++m_RenderDataVersion;
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        public void EditScaleSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Vector3 scale)
        {
            if (!EnsureEditingBuffers()) return;
            if (m_GpuEditPosMouseDown == null) return; // should have captured initial state

            using var cmb = new CommandBuffer { name = "SplatScaleSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.ScaleSelection);

            cmb.SetComputeBufferParam(EffectiveCSSplatUtilities, (int)KernelIndices.ScaleSelection, Props.SplatPosMouseDown, m_GpuEditPosMouseDown);
            cmb.SetComputeVectorParam(EffectiveCSSplatUtilities, Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(EffectiveCSSplatUtilities, Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(EffectiveCSSplatUtilities, Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(EffectiveCSSplatUtilities, Props.SelectionDelta, scale);

            DispatchUtilsAndExecute(cmb, KernelIndices.ScaleSelection, m_SplatCount);
            ++m_RenderDataVersion;
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        public void EditDeleteSelected()
        {
            if (!EnsureEditingBuffers()) return;
            UnionGraphicsBuffers(m_GpuEditDeleted, m_GpuEditSelected);
            ++m_RenderDataVersion;
            EditDeselectAll();
            UpdateEditCountsAndBounds();
            if (editDeletedSplats != 0)
                editModified = true;
        }

        public void EditSelectAll()
        {
            if (!EnsureEditingBuffers()) return;
            using var cmb = new CommandBuffer { name = "SplatSelectAll" };
            SetAssetDataOnCS(cmb, KernelIndices.SelectAll);
            cmb.SetComputeBufferParam(EffectiveCSSplatUtilities, (int)KernelIndices.SelectAll, Props.DstBuffer, m_GpuEditSelected);
            cmb.SetComputeIntParam(EffectiveCSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            DispatchUtilsAndExecute(cmb, KernelIndices.SelectAll, m_GpuEditSelected.count);
            UpdateEditCountsAndBounds();
        }

        public void EditDeselectAll()
        {
            if (!EnsureEditingBuffers()) return;
            ClearGraphicsBuffer(m_GpuEditSelected);
            UpdateEditCountsAndBounds();
        }

        public void EditInvertSelection()
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatInvertSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.InvertSelection);
            cmb.SetComputeBufferParam(EffectiveCSSplatUtilities, (int)KernelIndices.InvertSelection, Props.DstBuffer, m_GpuEditSelected);
            cmb.SetComputeIntParam(EffectiveCSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            DispatchUtilsAndExecute(cmb, KernelIndices.InvertSelection, m_GpuEditSelected.count);
            UpdateEditCountsAndBounds();
        }

        public bool EditExportData(GraphicsBuffer dstData, bool bakeTransform)
        {
            if (!EnsureEditingBuffers()) return false;

            int flags = 0;
            var tr = transform;
            Quaternion bakeRot = tr.localRotation;
            Vector3 bakeScale = tr.localScale;

            if (bakeTransform)
                flags = 1;

            using var cmb = new CommandBuffer { name = "SplatExportData" };
            SetAssetDataOnCS(cmb, KernelIndices.ExportData);
            cmb.SetComputeIntParam(EffectiveCSSplatUtilities, "_ExportTransformFlags", flags);
            cmb.SetComputeVectorParam(EffectiveCSSplatUtilities, "_ExportTransformRotation", new Vector4(bakeRot.x, bakeRot.y, bakeRot.z, bakeRot.w));
            cmb.SetComputeVectorParam(EffectiveCSSplatUtilities, "_ExportTransformScale", bakeScale);
            cmb.SetComputeMatrixParam(EffectiveCSSplatUtilities, Props.MatrixObjectToWorld, tr.localToWorldMatrix);
            cmb.SetComputeBufferParam(EffectiveCSSplatUtilities, (int)KernelIndices.ExportData, "_ExportBuffer", dstData);

            DispatchUtilsAndExecute(cmb, KernelIndices.ExportData, m_SplatCount);
            return true;
        }

        public void EditSetSplatCount(int newSplatCount)
        {
            if (!CanEditSplats) return;
            if (newSplatCount <= 0 || newSplatCount > GaussianSplat3DAsset.kMaxSplats)
            {
                Debug.LogError($"Invalid new splat count: {newSplatCount}");
                return;
            }
            if (asset.chunkData != null)
            {
                Debug.LogError("Only splats with VeryHigh quality can be resized");
                return;
            }
            if (newSplatCount == splatCount)
                return;

            int posStride = (int)(asset.posData.dataSize / asset.splatCount);
            int otherStride = (int)(asset.otherData.dataSize / asset.splatCount);
            int shStride = (int) (asset.shData.dataSize / asset.splatCount);

            var newPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * posStride / 4, 4) { name = "GaussianPosData" };
            var newOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * otherStride / 4, 4) { name = "GaussianOtherData" };
            var newSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, newSplatCount * shStride / 4, 4) { name = "GaussianSHData" };

            // new texture is a RenderTexture so we can write to it from a compute shader
            var (texWidth, texHeight) = GaussianSplat3DAsset.CalcTextureSize(newSplatCount);
            var texFormat = GaussianSplat3DAsset.ColorFormatToGraphics(asset.colorFormat);
            var newColorData = new RenderTexture(texWidth, texHeight, texFormat, GraphicsFormat.None) { name = "GaussianColorData", enableRandomWrite = true };
            newColorData.Create();

            var selTarget = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource | GraphicsBuffer.Target.CopyDestination;
            var selSize = (newSplatCount + 31) / 32;
            var newEditSelected = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatSelected"};
            var newEditSelectedMouseDown = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatSelectedInit"};
            var newEditDeleted = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatDeleted"};
            ClearGraphicsBuffer(newEditSelected);
            ClearGraphicsBuffer(newEditSelectedMouseDown);
            ClearGraphicsBuffer(newEditDeleted);

            EditCopySplats(transform, newPosData, newOtherData, newSHData, newColorData, newEditDeleted, newSplatCount, 0, 0, m_SplatCount);

            m_GpuPosData.Dispose();
            m_GpuOtherData.Dispose();
            m_GpuSHData.Dispose();
            if (m_GpuColorData != Texture2D.blackTexture) DestroyImmediate(m_GpuColorData);
            m_GpuColorData = null;
            DisposeCameraRenderResources();

            m_GpuEditSelected?.Dispose();
            m_GpuEditSelectedMouseDown?.Dispose();
            m_GpuEditDeleted?.Dispose();

            m_GpuPosData = newPosData;
            m_GpuOtherData = newOtherData;
            m_GpuSHData = newSHData;
            m_GpuColorData = newColorData;
            m_GpuEditSelected = newEditSelected;
            m_GpuEditSelectedMouseDown = newEditSelectedMouseDown;
            m_GpuEditDeleted = newEditDeleted;

            DisposeBuffer(ref m_GpuEditPosMouseDown);
            DisposeBuffer(ref m_GpuEditOtherMouseDown);

            m_SplatCount = newSplatCount;
            ++m_RenderDataVersion;
            editModified = true;
        }

        public void EditCopySplatsInto(GaussianSplat3DRenderer dst, int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            if (!CanEditSplats || !dst || !dst.CanEditSplats) return;
            EditCopySplats(
                dst.transform,
                dst.m_GpuPosData, dst.m_GpuOtherData, dst.m_GpuSHData, dst.m_GpuColorData, dst.m_GpuEditDeleted,
                dst.splatCount,
                copySrcStartIndex, copyDstStartIndex, copyCount);
            ++dst.m_RenderDataVersion;
            dst.editModified = true;
        }

        public void EditCopySplats(
            Transform dstTransform,
            GraphicsBuffer dstPos, GraphicsBuffer dstOther, GraphicsBuffer dstSH, Texture dstColor,
            GraphicsBuffer dstEditDeleted,
            int dstSize,
            int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            if (!EnsureEditingBuffers()) return;

            Matrix4x4 copyMatrix = dstTransform.worldToLocalMatrix * transform.localToWorldMatrix;
            Quaternion copyRot = copyMatrix.rotation;
            Vector3 copyScale = copyMatrix.lossyScale;

            using var cmb = new CommandBuffer { name = "SplatCopy" };
            SetAssetDataOnCS(cmb, KernelIndices.CopySplats);

            cmb.SetComputeBufferParam(EffectiveCSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstPos", dstPos);
            cmb.SetComputeBufferParam(EffectiveCSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstOther", dstOther);
            cmb.SetComputeBufferParam(EffectiveCSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstSH", dstSH);
            cmb.SetComputeTextureParam(EffectiveCSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstColor", dstColor);
            cmb.SetComputeBufferParam(EffectiveCSSplatUtilities, (int)KernelIndices.CopySplats, "_CopyDstEditDeleted", dstEditDeleted);

            cmb.SetComputeIntParam(EffectiveCSSplatUtilities, "_CopyDstSize", dstSize);
            cmb.SetComputeIntParam(EffectiveCSSplatUtilities, "_CopySrcStartIndex", copySrcStartIndex);
            cmb.SetComputeIntParam(EffectiveCSSplatUtilities, "_CopyDstStartIndex", copyDstStartIndex);
            cmb.SetComputeIntParam(EffectiveCSSplatUtilities, "_CopyCount", copyCount);

            cmb.SetComputeVectorParam(EffectiveCSSplatUtilities, "_CopyTransformRotation", new Vector4(copyRot.x, copyRot.y, copyRot.z, copyRot.w));
            cmb.SetComputeVectorParam(EffectiveCSSplatUtilities, "_CopyTransformScale", copyScale);
            cmb.SetComputeMatrixParam(EffectiveCSSplatUtilities, "_CopyTransformMatrix", copyMatrix);

            DispatchUtilsAndExecute(cmb, KernelIndices.CopySplats, copyCount);
        }

        void DispatchUtilsAndExecute(CommandBuffer cmb, KernelIndices kernel, int count)
        {
            EffectiveCSSplatUtilities.GetKernelThreadGroupSizes((int)kernel, out uint gsX, out _, out _);
            cmb.DispatchCompute(EffectiveCSSplatUtilities, (int)kernel, (int)((count + gsX - 1)/gsX), 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);
        }

        public GraphicsBuffer GpuEditDeleted => m_GpuEditDeleted;
    }
}
