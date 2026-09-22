"""Versioned little-endian tensor files; no pickle in exported bundles."""

import json
import math
from pathlib import Path

import numpy as np

from .config import sha256

ATTRIBUTES = ("positions", "log_scales", "rotations_wxyz", "opacity_logits", "sh")


def write_json(path, value):
    Path(path).write_text(json.dumps(value, indent=2, allow_nan=False) + "\n")


class TensorWriter:
    def __init__(self, root):
        self.root = Path(root)
        self.tensors = {}

    def add(self, name, value):
        if name in self.tensors:
            raise ValueError("Duplicate tensor: " + name)
        if hasattr(value, "detach"):
            value = value.detach().cpu().numpy()
        value = np.asarray(value)
        dtype = "<i4" if value.dtype.kind in "iu" else "<f4"
        value = np.ascontiguousarray(value, dtype=dtype)
        if not np.isfinite(value).all():
            raise ValueError("Nonfinite tensor: " + name)
        path = self.root / "tensors" / (name + ".bin")
        path.parent.mkdir(parents=True, exist_ok=True)
        value.tofile(path)
        self.tensors[name] = {"path": path.relative_to(self.root).as_posix(),
                              "dtype": dtype, "shape": list(value.shape),
                              "byte_length": value.nbytes, "sha256": sha256(path)}
        return name


def read_ply(path):
    from plyfile import PlyData
    vertex = PlyData.read(str(path))["vertex"].data
    names = vertex.dtype.names

    def stack(fields):
        if any(name not in names for name in fields):
            raise ValueError("Missing PLY attributes: " + str(fields))
        return np.ascontiguousarray(np.stack([vertex[name] for name in fields], axis=-1), dtype=np.float32)

    rest = sorted([name for name in names if name.startswith("f_rest_")], key=lambda n: int(n[7:]))
    if rest != ["f_rest_" + str(i) for i in range(len(rest))] or len(rest) % 3:
        raise ValueError("PLY SH attributes must be contiguous, channel-major f_rest_0..N")
    coefficients = 1 + len(rest) // 3
    degree = math.isqrt(coefficients) - 1
    if (degree + 1) ** 2 != coefficients or degree > 3:
        raise ValueError("Export supports complete SH degrees 0 through 3")
    count = len(vertex)
    if count == 0:
        raise ValueError("Empty Gaussian cloud")
    dc = stack(["f_dc_0", "f_dc_1", "f_dc_2"])[:, None, :]
    sh_rest = stack(rest).reshape(count, 3, -1).transpose(0, 2, 1) if rest else np.empty((count, 0, 3), np.float32)
    result = dict(zip(ATTRIBUTES, (
        stack(["x", "y", "z"]), stack(["scale_0", "scale_1", "scale_2"]),
        stack(["rot_0", "rot_1", "rot_2", "rot_3"]), stack(["opacity"]),
        np.ascontiguousarray(np.concatenate((dc, sh_rest), axis=1)))))
    if any(not np.isfinite(value).all() for value in result.values()):
        raise ValueError("PLY contains nonfinite canonical attributes")
    if (np.linalg.norm(result["rotations_wxyz"], axis=1) < 1e-12).any():
        raise ValueError("PLY contains zero-length canonical quaternions")
    return result, degree
