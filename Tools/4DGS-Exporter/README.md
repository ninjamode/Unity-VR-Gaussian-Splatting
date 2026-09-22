# 4DGS exporter

Converts fine-stage `hustvl/4DGaussians` checkpoints into canonical Gaussian tensors, deformation weights and ONNX for Unity. No retraining or image dataset is needed.

## Install

Copy this whole directory to the training machine. Activate the scene's training environment and install the extra dependency:
```bash
# conda activate env or whatever
python -m pip install -r 4DGS-Exporter/requirements-export.txt
```
This uses the environment's PyTorch, NumPy and `plyfile`. Export runs on CPU and does not need ONNX Runtime.

## Export

Use the scene's saved arguments (these already contain the resolved training settings):

```bash
python /path/to/4DGS-Exporter/export_4dgs.py \
  --repo /path/to/4DGaussians \
  --cfg-args /path/to/output/bouncingballs/cfg_args \
  --output /path/to/exports/bouncingballs
```
Copy the **whole output folder** to your Unity machine and import it through **Tools → Gaussians → 4D → Create Splat Asset**. See the [Unity guide](../../Documentation~/4DGS.md).

- The latest complete fine-stage checkpoint is selected by default. Use `--iteration N` to choose another.
- For relocated checkpoints, add `--model /new/model/path`. It can also point directly to a folder containing `point_cloud.ply` and `deformation.pth`.
- `--config` optionally overrides saved settings with a Python config or resolved JSON. Precedence is repository defaults → saved arguments → optional config → repeatable `--set KEY=JSON`. Use it for incomplete saved arguments or deliberate overrides.
- Timing is optional: `--time-min` and `--time-max` record a known model-time range; `--duration-seconds` supplies a duration hint. Configure playback in Unity.
- Supported models use six HexPlanes and a ReLU MLP. `no_grid`, `static_mlp` and `empty_voxel` must be false; `grid_pe` must be zero. SH degrees 0–3 are supported; SH deformation requires degree 3.
- Output folders are never overwritten. `manifest.json` marks a successful export; after a failure, inspect `export_failure.json` if present and retry with a new output folder.

`tensors/` contains FP32 canonical attributes and checkpoint weights, including inactive weights. These are export inputs: Unity packs the runtime data during import. Keep the folder intact. Use `--help` for all options.
