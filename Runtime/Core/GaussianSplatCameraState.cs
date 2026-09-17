// SPDX-License-Identifier: MIT
using System;
using UnityEngine;
using UnityEngine.XR;

namespace Gaussians.Core
{
    /// <summary>Immutable matrices and viewport sizes for one camera render pass.</summary>
    public readonly struct GaussianSplatCameraState
    {
        public readonly Matrix4x4 View, Projection, RightView, RightProjection;
        public readonly Vector2Int ScreenSize, RightScreenSize;
        public readonly int ViewCount, EyeIndex;

        public GaussianSplatCameraState(Matrix4x4 view, Matrix4x4 projection,
            Matrix4x4 rightView, Matrix4x4 rightProjection, Vector2Int screenSize,
            Vector2Int rightScreenSize, int viewCount, int eyeIndex = 0)
        {
            if (viewCount < 1 || viewCount > 2)
                throw new ArgumentOutOfRangeException(nameof(viewCount), "Gaussian splats support one or two views per pass.");
            View = view;
            Projection = projection;
            RightView = rightView;
            RightProjection = rightProjection;
            ScreenSize = screenSize;
            RightScreenSize = rightScreenSize;
            ViewCount = viewCount;
            EyeIndex = eyeIndex;
        }

        // Overrides exist only during a render callback. RenderGraph owns its own
        // value snapshot, so another camera or XR pass cannot overwrite its matrices.
        static Camera s_Camera;
        static GaussianSplatCameraState s_State;

        public Scope Apply(Camera camera) => new Scope(camera, this);

        public readonly struct Scope : IDisposable
        {
            readonly Camera m_PreviousCamera;
            readonly GaussianSplatCameraState m_PreviousState;
            internal Scope(Camera camera, GaussianSplatCameraState state)
            {
                m_PreviousCamera = s_Camera;
                m_PreviousState = s_State;
                s_Camera = camera;
                s_State = state;
            }
            public void Dispose()
            {
                s_Camera = m_PreviousCamera;
                s_State = m_PreviousState;
            }
        }

        public static GaussianSplatCameraState ForCamera(Camera camera)
        {
            if (ReferenceEquals(camera, s_Camera))
                return s_State;

            // BiRP/HDRP retain their Camera API path. URP supplies XRPass matrices
            // through Apply, since Camera stereo matrices may be absent on visionOS.
            bool stereo = camera.stereoEnabled;
            var view = stereo ? camera.GetStereoViewMatrix(Camera.StereoscopicEye.Left) : camera.worldToCameraMatrix;
            var projection = stereo ? camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left) : camera.projectionMatrix;
            var rightView = stereo ? camera.GetStereoViewMatrix(Camera.StereoscopicEye.Right) : view;
            var rightProjection = stereo ? camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right) : projection;
            var size = new Vector2Int(
                stereo && XRSettings.eyeTextureWidth > 0 ? XRSettings.eyeTextureWidth : camera.pixelWidth,
                stereo && XRSettings.eyeTextureHeight > 0 ? XRSettings.eyeTextureHeight : camera.pixelHeight);
            return new GaussianSplatCameraState(view, projection, rightView, rightProjection, size, size,
                stereo ? 2 : 1, camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? 1 : 0);
        }
    }
}
