# 4D Gaussian Splatting

4DGS adds animated deformation to the 3D renderer. Supported models use the HexPlane + MLP architecture from [hustvl/4DGaussians](https://github.com/hustvl/4DGaussians).

## Quick start

1. Install this package as described in the [package README](../Readme.md), with Unity AI Inference 2.6.x.
2. Export your trained checkpoint using the [server exporter](../Tools/4DGS-Exporter/README.md). Copy the whole output folder to your Unity machine.
3. Open **Tools → Gaussians → 4D → Create Splat Asset**, select that folder and create the assets. The default destination is `Assets/Gaussians/4D`.
4. Assign `<model>-canonical3d.asset` to a **3D Splat Renderer** component and configure its [render-pipeline integration](3DGS.md).
5. Add a **4D Gaussian Deformation** component to the same GameObject and assign `<model>-inference.asset`.

## Playback

Use Play/Pause/Stop, seek, loop and speed controls in the deformation component. Set its duration and model-time range to match your training data; the checkpoint alone does not tell us these values.

Pause holds the current deformation. Disabling the component displays the undeformed canonical model. Only the matching canonical asset can be deformed; unrelated 3D models and 2DGS are not supported.

## Import and rendering

- Import the Native backend, ONNX backend, or both. The component can only use backends included at import. Performance varies, ONNX seems a lot faster on Quest 3, while it's a mixed bag on desktop
- Keep **Morton Order** enabled to share spatial ordering between rendering and deformation. Its performance benefit depends on the model and device.
- FP16 SH reduces rendering-buffer memory and bandwidth, with reduced coefficient precision. Canonical SH precision is chosen at import; animated SH precision is set on the component. Geometry and inference remain FP32. OPtimizing this is an open to do
- Imported assets share generated data files. Keep them together; use the import settings asset to reimport.
