# Unity Gaussians

Gaussian Splatting for Unity, focusing on Mixed Reality support. Works with 3DGS and 2DGS models, renders multipass and instance on common XR and VR headsets such as the Quest 3 and Apple Vision Pro. This project builds upon the great work in [aras-p/UnityGaussianSplatting]. Check that project for more related info.

## Usage

Install via the Unity Package manager:
1. Open package manager, click the plus button, select "Add package from git URL..."
2. Enter `https://github.com/ninjamode/UnityGaussians.git`

To import and create Gaussian Splat assets:
1. In the menu bar, go to Tools -> Unity Gaussians -> Create and select your Gaussian type
2. In the just opened editor window, select the optimized ply file, chose a quality level, and press "Create Asset"

To view Gaussians:
1. Add a Gaussian Splat Renderer component to a GameObject. Select 2D or 3D depending on the type you want to show
2. Select the imported asset

## 4DGS Support

4DGS rendering needs a conversion step before importing, to be done in the 4DGS training environment. The offline [4DGS server exporter](Tools/4DGS-Exporter/README.md) converts trained HexPlane deformation checkpoints into portable FP32 tensors to be loaded into Unity.

Transfer the `Tools/4DGS-Exporter` directory to the training environment and run `export_4dgs.py`. Check the command line help and Readme for details.

In Unity, with Unity AI Inference 2.6.x installed, use **Tools -> Gaussians -> 4D -> Create Splat Asset** to import. Display the imported **<model>-canonical3d.asset** with the existing **3D Gaussian Splat Renderer**. It renders independently of inference and has no playback information. Add one **4D Gaussian Deformation** component and assign its matching **<model>-inference.asset** for GPU deformation, Play/Pause/Stop, seeking, looping and speed controls. See the [4DGS Unity guide](Documentation~/4DGS.md) for more info.

## Limitations

- Gaussians are still not super fast on XR devices. Benchmark your stuff.
- Needs D3D12, Metal or Vulkan graphics APIs.
- Only tested for desktop and XR devices.

## Acknowledgements

The 3D Gaussian Splatting implementation in this package is based on
[aras-p/UnityGaussianSplatting], originally developed by Aras Pranckevičius.

This package contains substantial modifications to the original renderer, including instanced rendering, direct render path and linear color support.

2DGS support is based on that project too, with modified loading and displaying logic.
