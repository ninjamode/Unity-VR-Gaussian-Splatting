"""Offline contract tests; set FOURDGS_REFERENCE_REPO for ONNX integration tests."""

import argparse
import json
import math
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import numpy as np
import torch
from plyfile import PlyData, PlyElement

import export_4dgs
from fourdgs_export.bundle import ATTRIBUTES, TensorWriter, read_ply
from fourdgs_export.config import find_checkpoint, import_reference, read_namespace, resolve_config, resolve_model, sha256

def checked_path(root, relative):
    root = Path(root).resolve()
    path = (root / relative).resolve()
    try:
        path.relative_to(root)
    except ValueError:
        raise ValueError("Bundle path escapes its directory: " + relative)
    return path


def read_tensor(root, descriptor):
    path = checked_path(root, descriptor["path"])
    dtype = descriptor["dtype"]
    shape = descriptor["shape"]
    if dtype not in ("<f4", "<i4") or any(type(v) is not int or v < 0 for v in shape):
        raise ValueError("Invalid tensor descriptor: " + str(path))
    expected = math.prod(shape) * 4
    if expected != descriptor["byte_length"] or path.stat().st_size != expected:
        raise ValueError("Tensor byte length mismatch: " + str(path))
    if sha256(path) != descriptor["sha256"]:
        raise ValueError("Tensor checksum mismatch: " + str(path))
    value = np.fromfile(path, dtype=dtype).reshape(shape)
    if not np.isfinite(value).all():
        raise ValueError("Nonfinite tensor: " + str(path))
    return value



def make_ply(path, count=11, degree=3):
    rng = np.random.RandomState(42)
    fields = ["x", "y", "z"] + ["f_dc_%d" % i for i in range(3)]
    fields += ["f_rest_%d" % i for i in range(3 * ((degree + 1)**2 - 1))]
    fields += ["opacity"] + ["scale_%d" % i for i in range(3)] + ["rot_%d" % i for i in range(4)]
    data = np.empty(count, dtype=[(name, "f4") for name in fields])
    for field in fields:
        data[field] = rng.uniform(-0.3, 0.3, count)
    data["rot_0"] = 2.0  # Deliberately unnormalized; never normalize before deformation.
    PlyData([PlyElement.describe(data, "vertex")], byte_order="<").write(str(path))
    return data


class BundleTests(unittest.TestCase):
    def test_model_from_saved_arguments_and_relocation_override(self):
        with tempfile.TemporaryDirectory() as directory:
            repo = Path(directory)
            model = repo / "output/scene"
            model.mkdir(parents=True)
            saved = model / "cfg_args"
            saved.write_text("Namespace(model_path='output/scene')")
            self.assertEqual(resolve_model(repo, cfg_args=saved)[0], model.resolve())
            saved.write_text("Namespace(model_path='/missing/server/path')")
            with self.assertRaisesRegex(ValueError, "relocated"):
                resolve_model(repo, cfg_args=saved)
            self.assertEqual(resolve_model(repo, model=model, cfg_args=saved), (model, saved))

    def test_binary_roundtrip_and_corruption(self):
        with tempfile.TemporaryDirectory() as directory:
            writer = TensorWriter(directory)
            expected = np.arange(24, dtype=np.float64).reshape(2, 3, 4).transpose(1, 0, 2)
            writer.add("weights/test", expected)
            descriptor = writer.tensors["weights/test"]
            np.testing.assert_array_equal(read_tensor(directory, descriptor), expected)
            self.assertEqual(descriptor["dtype"], "<f4")
            path = Path(directory) / descriptor["path"]
            content = bytearray(path.read_bytes())
            content[0] ^= 1
            path.write_bytes(content)
            with self.assertRaisesRegex(ValueError, "checksum"):
                read_tensor(directory, descriptor)
            path.write_bytes(content[:-1])
            with self.assertRaisesRegex(ValueError, "length"):
                read_tensor(directory, descriptor)

    def test_ply_preserves_raw_values_and_sh_order(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "point_cloud.ply"
            source = make_ply(path)
            actual, degree = read_ply(path)
            self.assertEqual(degree, 3)
            np.testing.assert_array_equal(actual["rotations_wxyz"][:, 0], source["rot_0"])
            np.testing.assert_array_equal(actual["log_scales"][:, 0], source["scale_0"])
            np.testing.assert_array_equal(actual["opacity_logits"][:, 0], source["opacity"])
            np.testing.assert_array_equal(actual["sh"][:, 1, 0], source["f_rest_0"])
            np.testing.assert_array_equal(actual["sh"][:, 1, 1], source["f_rest_15"])
            np.testing.assert_array_equal(actual["sh"][:, 15, 2], source["f_rest_44"])
            make_ply(path, degree=0)
            self.assertEqual(read_ply(path)[0]["sh"].shape, (11, 1, 3))

    def test_namespace_does_not_execute_code(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "cfg_args"
            path.write_text("Namespace(no_dx=True, multires=[1, 2])")
            self.assertEqual(read_namespace(path), {"no_dx": True, "multires": [1, 2]})
            path.write_text("Namespace(no_dx=__import__('os').getcwd())")
            with self.assertRaises(ValueError):
                read_namespace(path)

    def test_fine_checkpoint_selection(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name in ("iteration_2", "iteration_10", "coarse_iteration_999"):
                path = root / "point_cloud" / name
                path.mkdir(parents=True)
                (path / "deformation.pth").touch()
                (path / "point_cloud.ply").touch()
            self.assertEqual(find_checkpoint(root, -1).name, "iteration_10")
            self.assertEqual(find_checkpoint(root, 2).name, "iteration_2")



REFERENCE = os.environ.get("FOURDGS_REFERENCE_REPO")


@unittest.skipUnless(REFERENCE, "Set FOURDGS_REFERENCE_REPO for reference/ONNX integration tests")
class IntegrationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.repo = Path(REFERENCE).resolve()
        self.model = self.root / "model"
        self.model.mkdir()
        self.config_path = self.root / "config.json"
        self.config_path.write_text(json.dumps({"ModelHiddenParams": {
            "kplanes_config": {"grid_dimensions": 2, "input_coordinate_dim": 4,
                               "output_coordinate_dim": 4, "resolution": [4, 5, 6, 3]},
            "multires": [1, 2], "net_width": 8, "defor_depth": 2,
            "no_do": False, "no_dshs": False}}))

    def prepare(self, overrides=(), degree=3):
        config, _ = resolve_config(self.repo, self.model, self.config_path, overrides)
        torch.manual_seed(5)
        reference = import_reference(self.repo)(argparse.Namespace(**config)).eval()
        torch.save(reference.state_dict(), self.model / "deformation.pth")
        make_ply(self.model / "point_cloud.ply", degree=degree)
        return ["--repo", str(self.repo), "--model", str(self.model), "--config", str(self.config_path),
                "--output", str(self.root / "bundle"), "--time-min", "0", "--time-max", "1",
                "--duration-seconds", "2"] + [arg for value in overrides for arg in ("--set", value)]

    def test_configuration_precedence_and_rejections(self):
        (self.model / "cfg_args").write_text("Namespace(net_width=16, no_dx=True, source_path='/unused')")
        config, provenance = resolve_config(self.repo, self.model, self.config_path, ["net_width=12"])
        self.assertEqual(config["net_width"], 12)
        self.assertTrue(config["no_dx"])
        self.assertEqual(provenance["value_origins"]["net_width"], "explicit override")
        with self.assertRaisesRegex(ValueError, "Unknown"):
            resolve_config(self.repo, self.model, self.config_path, ["typo=true"])
        with self.assertRaisesRegex(ValueError, "grid_pe"):
            resolve_config(self.repo, self.model, self.config_path, ["grid_pe=2"])

    def test_cfg_args_selection_without_config_or_timing(self):
        args = self.prepare()
        # Saved settings are sufficient, even if the original config is unavailable.
        config, _ = resolve_config(self.repo, self.model, self.config_path, [])
        self.config_path.unlink()
        for option in ("--model", "--config", "--time-min", "--time-max", "--duration-seconds"):
            index = args.index(option)
            del args[index:index+2]
        saved = self.model / "cfg_args"
        saved.write_text(str(argparse.Namespace(model_path=str(self.model), configs="/missing/training.py", **config)))
        args += ["--cfg-args", str(saved)]
        self.assertEqual(export_4dgs.main(args), 0)
        manifest = json.loads((self.root / "bundle/manifest.json").read_text())
        self.assertEqual(manifest["configuration"], config)
        self.assertFalse(any(value["kind"] == "config" for value in manifest["configuration_provenance"]["sources"]))
        self.assertEqual(manifest["version"], 2)
        self.assertIsNone(manifest["time"]["model_range"])
        self.assertIsNone(manifest["time"]["duration_seconds"])
        self.assertEqual(manifest["time"]["playback_owner"], "player")
        self.assertTrue(any(value["kind"] == "cfg_args" for value in manifest["configuration_provenance"]["sources"]))

    def test_partial_or_invalid_optional_time_is_rejected(self):
        args = self.prepare()
        index = args.index("--time-max")
        del args[index:index+2]
        self.assertEqual(export_4dgs.main(args), 1)
        self.assertFalse((self.root / "bundle").exists())
        args += ["--time-max", "nan"]
        self.assertEqual(export_4dgs.main(args), 1)

    def test_all_heads_multilevel_bundle_and_onnx_roundtrip(self):
        args = self.prepare()
        self.assertEqual(export_4dgs.main(args), 0)
        root = self.root / "bundle"
        manifest = json.loads((root / "manifest.json").read_text())
        self.assertEqual(manifest["active_heads"], list(ATTRIBUTES))
        self.assertEqual(set(manifest["tensors"]),
                         set(manifest["canonical"].values()) | set(manifest["weights"].values()))
        for descriptor in manifest["tensors"].values():
            read_tensor(root, descriptor)
        self.assertEqual(sha256(root / "deformation.onnx"), manifest["onnx"]["sha256"])
        self.assertNotIn("fixtures", manifest)
        self.assertFalse((root / "validation.json").exists())
        # Refusal must preserve every existing export byte.
        previous = (root / "manifest.json").read_bytes()
        self.assertEqual(export_4dgs.main(args), 1)
        self.assertEqual((root / "manifest.json").read_bytes(), previous)

    def test_multiplicative_rotation_and_disabled_heads(self):
        args = self.prepare(["apply_rotation=true", "no_dx=true", "no_ds=true", "no_do=true", "no_dshs=true"], degree=0)
        self.assertEqual(export_4dgs.main(args), 0)

    def test_all_heads_disabled_pruned_onnx_inputs(self):
        args = self.prepare(["no_dx=true", "no_ds=true", "no_dr=true", "no_do=true", "no_dshs=true"], degree=0)
        self.assertEqual(export_4dgs.main(args), 0)

    def test_failed_onnx_export_does_not_publish_manifest(self):
        args = self.prepare()
        with patch("fourdgs_export.model.export_onnx", side_effect=ValueError("ONNX export failed")):
            self.assertEqual(export_4dgs.main(args), 1)
        self.assertFalse((self.root / "bundle/manifest.json").exists())
        self.assertTrue((self.root / "bundle/export_failure.json").exists())


if __name__ == "__main__":
    unittest.main()
