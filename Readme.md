# Unity Gaussians

Gaussian Splatting for Unity, with a focus on Mixed Reality. Import and render trained 3DGS, 2DGS, and animated 4DGS models.

This adds performance improvements targeting XR, 2DGS and 4DGS support, instanced and foveated rendering, among others. Now distributed as a Unity package that can directly be installed and used.

**NOTE:**<br>
This is a research project building on [UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting). No support.

## Installation

Requires **Unity 6000.3 or later** and **D3D12, Metal or Vulkan**. Burst, Collections and Mathematics are installed as package dependencies. URP/HDRP integrations use Unity 6 render pipelines; 4D import and playback additionally require **Unity AI Inference 2.6.x**.

In the Unity Package Manager, choose **Add package from git URL** and enter:

```text
https://github.com/ninjamode/Unity-VR-Gaussian-Splatting.git
```


## Quick start

1. Open **Tools → Gaussians → 3D → Create Splat Asset**, select your trained Gaussian file, choose a quality preset and click **Create Asset**.
2. Add **Gaussians → 3D Splat Renderer** to a GameObject and assign the generated asset.
3. Configure the render pipeline. For URP, add **Gaussians** to the camera's renderer asset and keep Render Graph enabled. For HDRP, add **Gaussians HDRP Pass** to a Custom Pass Volume at **Before Transparent**. O

Use the [3DGS guide](Documentation~/3DGS.md) for further info. Keep generated data files with their assets.


## General

If multiple models are present they are sorted individually by default and do not interact nicely with each other. Add a **GaussiansGroup** component to combine them at some minor overhead.

**Device Support:** Works on desktop as well as Quest 3. Apple Vision Pro should be supported but is untested. Other VR systems should be fine too.

Built-in doesnt need any special configuration, but is slower. HDRP is mostly untested.

### 2DGS

Create a 2D asset and add **2D Splat Renderer**. Use the same **Gaussians** URP feature or **Gaussians HDRP Pass** as 3DGS; do not add another feature/pass for 2DGS.

### 4DGS

A little export helper is necessary to have all the data for runtime use in unity. Supported models use the HexPlane + MLP architecture from [hustvl/4DGaussians](https://github.com/hustvl/4DGaussians).

Export your trained checkpoint with the [offline exporter](Tools/4DGS-Exporter/README.md), then copy its whole output folder to your Unity machine. With Unity AI Inference installed, use **Tools → Gaussians → 4D → Create Splat Asset**.

Assign `<model>-canonical3d.asset` to a **3D Splat Renderer**, add **4D Gaussian Deformation** to the same GameObject and assign the matching `<model>-inference.asset`. The canonical model can render without inference. See the [4DGS guide](Documentation~/4DGS.md) for import and playback.

## Limitations

- WebGL and OpenGL graphics APIs are not supported.
- Lower sort precision and size/opacity pruning trade visual quality for reduced work. Check output on the intended device and pipeline.
- Selection editing can be flaky.

## Acknowledgements and license

The 3D implementation is derived from Aras Pranckevičius' [UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting). Thank you! The 2D renderer adapts the same foundation with different loading and display logic.

Package code is available under the [MIT license](LICENSE.md). See [third-party notices](THIRD%20PARTY%20NOTICES.md) for included software. Trained models and datasets retain their own licenses. AIs have been used for coding support, bad ideas are my own.
