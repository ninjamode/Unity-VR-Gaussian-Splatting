"""Load the trained deformation network and export its ONNX graph."""

import argparse
import inspect

import torch

from .bundle import ATTRIBUTES
from .config import import_reference, sha256

PLANES = ("xy", "xz", "xt", "yz", "yt", "zt")
HEADS = (("positions", "pos_deform", "no_dx"),
         ("log_scales", "scales_deform", "no_ds"),
         ("rotations_wxyz", "rotations_deform", "no_dr"),
         ("opacity_logits", "opacity_deform", "no_do"),
         ("sh", "shs_deform", "no_dshs"))
INPUTS = ATTRIBUTES + ("times",)
OUTPUTS = tuple("deformed_" + name for name in ATTRIBUTES)


def load_reference(repo, config, checkpoint):
    reference = import_reference(repo)(argparse.Namespace(**config))
    kwargs = {"map_location": "cpu"}
    if "weights_only" in inspect.signature(torch.load).parameters:
        kwargs["weights_only"] = True
    state = torch.load(str(checkpoint / "deformation.pth"), **kwargs)
    if not isinstance(state, dict) or any(not isinstance(v, torch.Tensor) for v in state.values()):
        raise ValueError("Expected a deformation state_dict; training snapshots are not supported")
    reference.load_state_dict(state, strict=True)
    for name, value in state.items():
        if value.dtype != torch.float32 or not torch.isfinite(value).all():
            raise ValueError("Expected finite FP32 checkpoint tensor: " + name)
    aabb = state["deformation_net.grid.aabb"]
    if aabb.shape != (2, 3) or not torch.all(aabb[0] > aabb[1]):
        raise ValueError("Expected nondegenerate AABB in [maximum, minimum] order")
    reference.eval().requires_grad_(False)
    return reference, state


class ReferenceExport(torch.nn.Module):
    def __init__(self, reference):
        super().__init__()
        self.reference = reference

    def forward(self, positions, log_scales, rotations_wxyz, opacity_logits, sh, times):
        # Independent output names even when a disabled head passes an input through.
        return tuple(value.clone() for value in self.reference(
            positions, log_scales, rotations_wxyz, opacity_logits, sh, times))


def tensor_inputs(canonical, times):
    return tuple(torch.as_tensor(canonical[name], dtype=torch.float32) for name in ATTRIBUTES) + (
        torch.as_tensor(times, dtype=torch.float32),)


def export_onnx(reference, example, path):
    import onnx
    options = {"opset_version": 16, "input_names": list(INPUTS), "output_names": list(OUTPUTS),
               "dynamic_axes": {name: {0: "gaussian_count"} for name in INPUTS + OUTPUTS},
               "do_constant_folding": True}
    # Keep compatibility with the upstream torch 1.13 environment and use the same
    # exporter on newer torch versions, whose default is now dynamo=True.
    if "dynamo" in inspect.signature(torch.onnx.export).parameters:
        options["dynamo"] = False
    torch.onnx.export(ReferenceExport(reference), example, str(path), **options)
    model = onnx.load(str(path), load_external_data=False)
    if any(t.data_location == onnx.TensorProto.EXTERNAL for t in model.graph.initializer):
        raise ValueError("Export requires a self-contained ONNX file (external tensor data is unsupported)")
    onnx.checker.check_model(model, full_check=True)
    return {"path": path.name, "sha256": sha256(path), "opset": 16,
            "operators": sorted(set(node.op_type for node in model.graph.node)),
            "inputs": [value.name for value in model.graph.input],
            "outputs": [value.name for value in model.graph.output],
            "output_semantics": "raw attributes; apply exp, normalize and sigmoid after inference"}
