# 3D Gaussian Splatting

Import trained 3DGS models and display them with the 3D renderer. See the [package README](../Readme.md) for installation and requirements.

## Quick start

1. Open **Tools → Gaussians → 3D → Create Splat Asset**.
2. Select a trained Gaussian `.ply` or version 2 `.spz` file, choose a quality preset and output folder, then click **Create Asset**. The default destination is `Assets/Gaussians/3D`.
3. Add **Gaussians → 3D Splat Renderer** to a GameObject and assign the generated `.asset`. Keep its generated `.bytes` files together with it.
4. Configure the pipeline: Built-in uses camera hooks automatically. URP needs the **Gaussians** feature on the camera's renderer asset with Render Graph enabled. HDRP needs the **Gaussians HDRP Pass** in a Custom Pass Volume at **Before Transparent**. The same entry also handles 2DGS and 4DGS, and likely any future additions.

Quality presets trade storage and precision. Higher-quality imports reduce compression artifacts; runtime SH order is a separate setting.

## Recommendations for XR
Enable:
- Instanced Rendering
- SRP Foveation
- Symmetric Projection & Miltiview Render Regions Optimization

## Rendering settings

- **16-bit Sorting:** optional lower depth-key precision with fewer sorting passes. It can reduce cost, but tied depths can cause ordering artifacts. Start with the default 32-bit sorting and compare quality and timings on your device. Larger **Sort Every N Frames** intervals can also leave stale order during motion.
- **Minimum Splat Radius:** defaults to 0.7 render-target pixels. This removes small splats and can lose detail; zero disables size pruning. **Minimum View Depth** rejects centers closer than its threshold or the camera near plane.
- **Compaction Threshold:** enables compacted sorting and deferred SH loading using a recent rejected-splat estimate. `-1` disables both, `0` always enables them; the default is 100,000. Benchmark this heuristic for your model.
- **Advanced Options:** Direct Transparent is the default render path; Composite Texture uses offscreen accumulation. Keep gamma-to-linear conversion enabled for gamma-encoded colors in a Linear project. Models do not share a global per-splat sort order.
- **Alpha Cutoff / Write Depth:** cutoff removes faint contributions. URP depth writing supplies approximate splat-center depth for XR reprojection without changing color blending.
- **Quest foveation:** Keep Subsampled Layout disabled if it causes black regions, particularly with Symmetric Projection on Quest 3.

Check output [4DGS](4DGS.md) for animated deformation through this renderer.
