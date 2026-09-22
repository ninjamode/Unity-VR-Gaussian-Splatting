#!/usr/bin/env python3
"""Export a fine-stage hustvl/4DGaussians checkpoint for Unity neural playback."""

import argparse
import math
import sys
from pathlib import Path


def parser():
    result = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.ArgumentDefaultsHelpFormatter)
    result.add_argument("--repo", type=Path, required=True, help="Original 4DGaussians repository")
    result.add_argument("--cfg-args", type=Path, help="Scene's saved cfg_args; defaults to cfg_args in the working directory")
    result.add_argument("--model", type=Path, help="Optional model path override for relocated checkpoints")
    result.add_argument("--iteration", type=int, default=-1, help="Fine iteration; -1 selects the latest complete checkpoint")
    result.add_argument("--config", type=Path, help="Optional training Python config or resolved JSON overriding saved settings")
    result.add_argument("--set", action="append", default=[], metavar="KEY=JSON", help="Explicit ModelHiddenParams override; repeatable")
    result.add_argument("--output", type=Path, required=True, help="New output directory (never overwritten)")
    result.add_argument("--time-min", type=float, help="Optional known model time range, paired with --time-max")
    result.add_argument("--time-max", type=float, help="Optional known model time range; unknown by default")
    result.add_argument("--duration-seconds", type=float, help="Optional suggested duration; playback timing belongs to the player")
    return result


def validate_arguments(args):
    from fourdgs_export.config import resolve_model
    for key in ("repo", "model", "cfg_args", "config", "output"):
        if getattr(args, key) is not None:
            setattr(args, key, getattr(args, key).expanduser().resolve())
    args.model, args.cfg_args = resolve_model(args.repo, args.model, args.cfg_args)
    values = (args.time_min, args.time_max, args.duration_seconds)
    if not all(value is None or math.isfinite(value) for value in values):
        raise ValueError("Time arguments must be finite")
    if (args.time_min is None) != (args.time_max is None):
        raise ValueError("Supply both --time-min and --time-max, or omit both")
    if args.time_min is not None and args.time_min >= args.time_max:
        raise ValueError("Require time-min < time-max")
    if args.duration_seconds is not None and args.duration_seconds <= 0:
        raise ValueError("Require duration-seconds > 0 when supplied")
    if args.iteration < -1:
        raise ValueError("Iteration must be >= -1")
    if not args.repo.is_dir() or not args.model.is_dir():
        raise ValueError("repo/model must be existing directories")
    if args.config is not None and not args.config.is_file():
        raise ValueError("config must be an existing file when supplied")
    if args.output.exists():
        raise ValueError("Output already exists; choose a new directory: " + str(args.output))


def export(args):
    import numpy as np
    import torch
    from fourdgs_export import FORMAT, VERSION
    from fourdgs_export.bundle import TensorWriter, read_ply, write_json
    from fourdgs_export.config import find_checkpoint, repository_provenance, resolve_config, sha256
    from fourdgs_export.model import HEADS, PLANES, export_onnx, load_reference, tensor_inputs

    torch.set_num_threads(1)
    config, provenance = resolve_config(args.repo, args.model, args.config, args.set, args.cfg_args)
    checkpoint = find_checkpoint(args.model, args.iteration)
    canonical, sh_degree = read_ply(checkpoint / "point_cloud.ply")
    if not config["no_dshs"] and sh_degree != 3:
        raise ValueError("The reference SH deformation head requires degree 3 (16 RGB coefficients)")
    reference, state = load_reference(args.repo, config, checkpoint)
    print("Checkpoint:", checkpoint, flush=True)
    print("Gaussians: %d; SH degree: %d" % (len(canonical["positions"]), sh_degree), flush=True)
    print("Active heads:", [name for name, _, disabled in HEADS if not config[disabled]], flush=True)
    print("Config origins and resolved values will be recorded in configuration.json", flush=True)
    if provenance["ignored_scene_config_keys"]:
        print("Ignored config keys (matching upstream merge_hparams):", provenance["ignored_scene_config_keys"], flush=True)

    # Dependencies must be present before creating an incomplete export directory.
    import onnx
    args.output.mkdir(parents=True, exist_ok=False)
    try:
        writer = TensorWriter(args.output)
        model_range = None if args.time_min is None else [args.time_min, args.time_max]
        manifest = {"format": FORMAT, "version": VERSION,
                    "model_family": "hustvl/4DGaussians HexPlane deformation",
                    "gaussian_count": len(canonical["positions"]), "sh_degree": sh_degree,
                    "configuration": config, "configuration_provenance": provenance,
                    "source": {"repository": repository_provenance(args.repo), "checkpoint": str(checkpoint),
                               "sha256": {name: sha256(checkpoint / name) for name in ("point_cloud.ply", "deformation.pth")}},
                    "time": {"model_range": model_range, "duration_seconds": args.duration_seconds,
                             "source": "explicit override" if model_range else "unknown; determined by training dataloader",
                             "duration_source": "optional CLI hint" if args.duration_seconds is not None else "unspecified",
                             "playback_owner": "player"},
                    "conventions": {"coordinates": "unchanged source coordinates; object transform applied after deformation",
                                    "point_order": "original PLY vertex order",
                                    "quaternion": "wxyz; raw canonical values, normalize after deformation",
                                    "scales": "natural logarithm; exp after deformation",
                                    "opacity": "logit; sigmoid after deformation",
                                    "sh": "[gaussian, coefficient, RGB]; DC remains an SH coefficient",
                                    "aabb_order": ["maximum", "minimum"],
                                    "spatial_normalization": "(xyz - aabb[0]) * (2 / (aabb[1] - aabb[0])) - 1",
                                    "time_normalization": "network receives model time unchanged; player supplies the mapping",
                                    "plane_order": list(PLANES), "plane_layout": "[1, channels, second_axis_resolution, first_axis_resolution]",
                                    "sampling": {"mode": "bilinear", "padding": "border", "align_corners": True},
                                    "feature_combination": "elementwise product of six planes per level; concatenate levels",
                                    "linear_weights": "[output, input]; y = x @ weight.T + bias",
                                    "deformation_table": "not applied; matches active upstream all-point forward path",
                                    "deformation_accum": "training statistics; not exported",
                                    "bounds": "no motion bounds inferred; canonical AABB is only for network normalization"},
                    "canonical": {name: writer.add("canonical/" + name, value) for name, value in canonical.items()},
                    "weights": {name: writer.add("weights/" + name, value) for name, value in state.items()},
                    "active_heads": [name for name, _, disabled in HEADS if not config[disabled]],
                    "weights_scope": "complete reference state_dict, including unused timenet and disabled heads",
                    "tensors": writer.tensors}
        write_json(args.output / "configuration.json", {"ModelHiddenParams": config, "provenance": provenance})
        n = min(7, len(canonical["positions"]))
        example = tensor_inputs({name: value[:n] for name, value in canonical.items()},
                                np.full((n, 1), sum(model_range) / 2 if model_range else 0.5, np.float32))
        print("Exporting ONNX (opset 16, dynamic Gaussian count)...", flush=True)
        manifest["onnx"] = export_onnx(reference, example, args.output / "deformation.onnx")
        write_json(args.output / "manifest.json", manifest)  # Success marker, always written last.
        print("Exported %d Gaussians to %s" % (manifest["gaussian_count"], args.output), flush=True)
    except Exception as error:
        write_json(args.output / "export_failure.json", {"error": str(error), "type": type(error).__name__,
                                                       "usable_bundle": False})
        raise


def main(argv=None):
    args = parser().parse_args(argv)
    try:
        validate_arguments(args)
        export(args)
    except Exception as error:
        print("Export failed: %s: %s" % (type(error).__name__, error), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
