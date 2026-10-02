# Unity Gaussians

Gaussian Splatting for Unity, with a focus on Mixed Reality. Import and render trained 3DGS and 2DGS models, or animate supported 4DGS models through the 3D renderer. Includes single-pass instanced stereo rendering with foveation support.

This package builds on [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting), with changes for improved performance especially in XR.

**NOTE**<br>
This is a research project. No support.

## Installation

Requires **Unity 6000.3 or later** and **D3D12, Metal or Vulkan**. Burst, Collections and Mathematics are installed as package dependencies. URP/HDRP integrations use Unity 6 render pipelines; 4D import and playback additionally require **Unity AI Inference 2.6.x**.

In the Unity Package Manager, choose **Add package from git URL** and enter:

```text
https://github.com/ninjamode/UnityGaussians.git
```

For a local checkout, choose **Add package from disk** and select this folder's `package.json`.

## Quick start

1. Open **Tools → Gaussians → 3D → Create Splat Asset** or **2D → Create Splat Asset**, select your trained Gaussian file, choose a quality preset and click **Create Asset**.
2. Add **Gaussians → 3D Splat Renderer** or **2D Splat Renderer** to a GameObject and assign the generated asset.
3. Configure the render pipeline. Built-in uses camera hooks automatically. For URP, add **Gaussian Splat 3D URP Feature** or **Gaussian Splat 2D URP Feature** to the camera's renderer asset and keep Render Graph enabled. For HDRP, add the matching **Gaussian Splat 3D/2D HDRP Pass** to a Custom Pass Volume before transparent rendering.

Use the [3DGS guide](Documentation~/3DGS.md) for import formats, rendering settings and sorting considerations. Keep generated data files with their assets.

## Device Support

Works on desktop as well as Quest 3. Apple Vision Pro should be supported but is untested.

## 4DGS

A little export helper is necessary to have all the data for runtime use in unity. Supported models use the HexPlane + MLP architecture from [hustvl/4DGaussians](https://github.com/hustvl/4DGaussians).

Export your trained checkpoint with the [offline exporter](Tools/4DGS-Exporter/README.md), then copy its whole output folder to your Unity machine. With Unity AI Inference installed, use **Tools → Gaussians → 4D → Create Splat Asset**.

Assign `<model>-canonical3d.asset` to a **3D Splat Renderer**, add **4D Gaussian Deformation** to the same GameObject and assign the matching `<model>-inference.asset`. The canonical model can render without inference. See the [4DGS guide](Documentation~/4DGS.md) for import and playback.

## Limitations

- WebGL and OpenGL graphics APIs are not supported.
- Models are sorted individually. Overlapping renderer objects do not share a global per-splat order.
- Lower sort precision and size/opacity pruning trade visual quality for reduced work. Check output on the intended device and pipeline.
- Selection editing can be flaky.

## Acknowledgements and license

The 3D implementation is derived from Aras Pranckevičius' [UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting). Thank you! The 2D renderer adapts the same foundation with different loading and display logic.

Package code is available under the [MIT license](LICENSE.md). See [third-party notices](THIRD%20PARTY%20NOTICES.md) for included software. Trained models and datasets retain their own licenses.
