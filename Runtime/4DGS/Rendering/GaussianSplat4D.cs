using System;
using UnityEngine;
using Gaussians.ThreeD;
using Gaussians.FourD.Inference;

namespace Gaussians.FourD
{
    [ExecuteAlways, DisallowMultipleComponent, RequireComponent(typeof(GaussianSplat3DRenderer))]
    [AddComponentMenu("Gaussian Splats/4D Gaussian Deformation")]
    public sealed class GaussianSplat4D : MonoBehaviour, IGaussianSplat3DDeformation
    {
        public enum DeformationBackend { Onnx, Native }
        public GaussianSplat4DInferenceAsset m_Asset;
        public DeformationBackend m_Backend;
        [Tooltip("FP16 SH reduces rendering-buffer bandwidth. Geometry and inference remain FP32.")]
        public GaussianSplatSHStorage m_SHStorage;
        [Tooltip("Raw trained-model time. Playback maps its position into this value.")]
        public float m_ModelTime;
        public ComputeShader m_ComputeShader;
        public ComputeShader m_NativeShader;

        GaussianSplat4DEvaluator m_Evaluator;
        GaussianSplat4DInferenceAsset m_LoadedAsset;
        DeformationBackend m_LoadedBackend;
        GaussianSplatSHStorage m_LoadedStorage;
        ComputeShader m_Compute;
        int m_PreparedFrame = -1, m_LoadedRevision;
        bool m_Failed;
        GaussianSplat3DFrame m_Frame;
        public string Error { get; private set; }
        public int EvaluationCount => m_Evaluator?.EvaluationCount ?? 0;
        public string CompatibilityError => !m_Asset ? "Assign a 4D deformation asset."
            : !m_Asset.Matches(Renderer ? Renderer.asset : null) ? "The 3D renderer must use this deformation asset's unchanged canonical model."
            : BackendError;
        public string BackendError => !m_Asset || !m_Asset.Data ? null
            : m_Backend == DeformationBackend.Native && !m_Asset.Data.HasNative ? "Native backend was not included in this import."
            : m_Backend == DeformationBackend.Onnx && !m_Asset.Model ? "ONNX backend was not included in this import." : null;

        [Tooltip("Player-owned duration in seconds. Independent of export timing metadata.")]
        [Min(0.001f)] public float m_DurationSeconds = 5;
        [Tooltip("Raw model-time endpoints. Descending ranges are supported.")]
        public Vector2 m_ModelTimeRange = new(0,1);
        [Tooltip("Negative speed plays backwards; zero holds the current time.")]
        public float m_Speed = 1;
        public bool m_Loop = true;
        public bool m_PlayOnEnable = true;
        public bool m_UseUnscaledTime;

        GaussianSplat3DRenderer m_Renderer;
        double m_PositionSeconds;
        public bool IsPlaying { get; private set; }
        public double PositionSeconds => m_PositionSeconds;
        public double NormalizedTime => ValidDuration ? Math.Clamp(m_PositionSeconds / m_DurationSeconds,0,1) : 0;
        public float ModelTime => m_ModelTime;
        public GaussianSplat3DRenderer Renderer => m_Renderer ? m_Renderer : m_Renderer = GetComponent<GaussianSplat3DRenderer>();
        bool ValidDuration => Finite(m_DurationSeconds) && m_DurationSeconds > 0;
        public string SettingsError => !ValidDuration ? "Duration must be finite and greater than zero."
            : !Finite(m_ModelTimeRange.x) || !Finite(m_ModelTimeRange.y) ? "Model-time endpoints must be finite."
            : !Finite(m_Speed) ? "Speed must be finite." : null;

        void Reset() => ResolveShaders();
        void OnEnable()
        {
            ResolveShaders();
            if (Renderer) Renderer.SetDeformation(this);
            // Editor preview is explicitly driven by the custom Inspector.
            if (Application.IsPlaying(gameObject) && m_PlayOnEnable) Play();
        }
        void OnDisable()
        {
            Pause();
            if (Renderer) Renderer.SetDeformation(null);
            Release();
        }

        void ResolveShaders()
        {
#if UNITY_EDITOR
            const string path = "Packages/net.kleinbeck.gaussians/Shaders/4DGS/";
            if (!m_ComputeShader) m_ComputeShader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(path + "GaussianSplat4D.compute");
            if (!m_NativeShader) m_NativeShader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(path + "NativeDeformation.compute");
#endif
        }

        [ContextMenu("Rebuild deformation resources")]
        public void Rebuild()
        {
            if (Renderer) Renderer.SetDeformation(null);
            Release();
            Error = null; m_Failed = false;
            if (isActiveAndEnabled && Renderer) Renderer.SetDeformation(this);
        }
        void Release()
        {
            m_Evaluator?.Dispose(); m_Evaluator = null;
            if (m_Compute) { if (Application.isPlaying) Destroy(m_Compute); else DestroyImmediate(m_Compute); }
            m_Compute = null; m_LoadedAsset = null; m_Frame = default; m_PreparedFrame = -1;
        }

        public bool TryGetFrame(GaussianSplat3DAsset canonical, out GaussianSplat3DFrame frame)
        {
            frame = default;
            if (!isActiveAndEnabled) return false;
            if (m_LoadedAsset != m_Asset || m_LoadedBackend != m_Backend || m_LoadedStorage != m_SHStorage || m_LoadedRevision != (m_Asset ? m_Asset.Revision : 0))
            {
                Rebuild();
                m_LoadedAsset = m_Asset; m_LoadedBackend = m_Backend; m_LoadedStorage = m_SHStorage; m_LoadedRevision = m_Asset ? m_Asset.Revision : 0;
            }
            string compatibility = !m_Asset ? "Assign a 4D deformation asset."
                : !m_Asset.Matches(canonical) ? "4D deformation rejected: assign its unchanged canonical model to the 3D renderer." : null;
            if (compatibility != null)
            {
                if (Error != compatibility) { Error = compatibility; Debug.LogWarning(compatibility, this); }
                return false;
            }
            if (BackendError != null) { Error = BackendError; return false; }
            if (m_Failed) return false;
            // A corrected compatibility warning is not a failed backend load.
            Error = null;
            if (!float.IsFinite(m_ModelTime))
            {
                Error = "Model time must be finite.";
                return false;
            }
            try
            {
                if (m_Evaluator == null)
                {
                    m_LoadedAsset = m_Asset; m_LoadedBackend = m_Backend; m_LoadedStorage = m_SHStorage; m_LoadedRevision = m_Asset.Revision;
                    if (!m_ComputeShader || (m_Backend == DeformationBackend.Native && !m_NativeShader))
                        throw new InvalidOperationException("Assign the deformation compute shaders.");
                    m_Compute = Instantiate(m_ComputeShader);
                    m_Compute.hideFlags = HideFlags.HideAndDontSave;
                    m_Evaluator = new GaussianSplat4DEvaluator(m_Asset, m_Compute,
                        m_Backend == DeformationBackend.Native ? m_NativeShader : null, m_SHStorage);
                }
                // Freeze one pose across every camera and eye in a runtime frame.
                // Editor scrubbing can request several poses without advancing frameCount.
                if (!Application.IsPlaying(gameObject) || m_PreparedFrame != Time.frameCount)
                {
                    m_Evaluator.Evaluate(m_ModelTime);
                    m_Frame = new GaussianSplat3DFrame(m_Evaluator.Data, m_Evaluator.PositionRevision, m_Evaluator.AttributeRevision);
                    m_PreparedFrame = Time.frameCount;
                }
                Error = null;
                frame = m_Frame;
                return frame.Data != null;
            }
            catch (Exception exception)
            {
                if (Renderer) Renderer.SetDeformation(null);
                Release();
                m_LoadedAsset = m_Asset; m_LoadedBackend = m_Backend; m_LoadedStorage = m_SHStorage; m_LoadedRevision = m_Asset.Revision;
                Error = exception.Message; m_Failed = true;
                if (Renderer) Renderer.SetDeformation(this);
                Debug.LogException(exception, this);
                return false;
            }
        }
        void Update()
        {
            if (Application.IsPlaying(gameObject)) Advance(m_UseUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime);
        }

        /// <summary>Resume playback. At a completed endpoint restart in the speed's direction.</summary>
        public bool Play()
        {
            if (!isActiveAndEnabled || SettingsError != null || !Renderer) { Pause(); return false; }
            m_PositionSeconds = Math.Clamp(m_PositionSeconds,0,m_DurationSeconds);
            if (m_Speed > 0 && m_PositionSeconds >= m_DurationSeconds) m_PositionSeconds = 0;
            if (m_Speed < 0 && m_PositionSeconds <= 0) m_PositionSeconds = m_DurationSeconds;
            IsPlaying = true;
            ApplyTime();
            return true;
        }

        public void Pause() => IsPlaying = false;

        /// <summary>Pause and rewind to the first model-time endpoint, even with negative speed.</summary>
        public void Stop()
        {
            Pause();
            m_PositionSeconds = 0;
            ApplyTime();
        }

        /// <summary>Clamp to the clip interval. Seeking preserves playing/paused state.</summary>
        public bool SeekSeconds(double seconds)
        {
            if (!Finite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
            if (SettingsError != null) { Pause(); return false; }
            m_PositionSeconds = Math.Clamp(seconds,0,m_DurationSeconds);
            ApplyTime();
            return true;
        }

        public bool SeekNormalized(double normalizedTime)
        {
            if (!Finite(normalizedTime)) throw new ArgumentOutOfRangeException(nameof(normalizedTime));
            if (SettingsError != null) { Pause(); return false; }
            return SeekSeconds(Math.Clamp(normalizedTime,0,1) * m_DurationSeconds);
        }

        /// <summary>Advance once by elapsed seconds. Runtime Update or explicit Editor preview
        /// calls this; camera callbacks never advance the clock.</summary>
        public void Advance(double deltaSeconds)
        {
            if (!Finite(deltaSeconds) || deltaSeconds < 0) throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
            if (!IsPlaying) return;
            if (!isActiveAndEnabled || SettingsError != null) { Pause(); return; }
            double next = Math.Clamp(m_PositionSeconds,0,m_DurationSeconds) + deltaSeconds*m_Speed;
            if (!Finite(next)) { Pause(); return; }
            if (m_Loop)
            {
                // Preserve an explicitly sought endpoint while holding speed/time at zero.
                if (deltaSeconds != 0 && m_Speed != 0)
                    next = ((next % m_DurationSeconds)+m_DurationSeconds)%m_DurationSeconds;
            }
            else if ((m_Speed > 0 && next >= m_DurationSeconds) || (m_Speed < 0 && next <= 0)) Pause();
            m_PositionSeconds = Math.Clamp(next,0,m_DurationSeconds);
            ApplyTime();
        }

        /// <summary>Reapply current position after changing duration or model-time range.</summary>
        public bool ApplyTime()
        {
            if (SettingsError != null || !Renderer) return false;
            m_PositionSeconds = Math.Clamp(m_PositionSeconds,0,m_DurationSeconds);
            m_ModelTime = (float)(m_ModelTimeRange.x + NormalizedTime * ((double)m_ModelTimeRange.y - m_ModelTimeRange.x));
            return true;
        }

        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
