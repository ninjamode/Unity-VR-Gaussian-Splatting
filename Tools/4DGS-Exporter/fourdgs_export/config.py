"""Training configuration resolution and original-module loading (no rasterizer)."""

import argparse
import ast
import hashlib
import importlib
import importlib.util
import json
import subprocess
import sys
import types
from pathlib import Path


def sha256(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def read_namespace(path):
    """Read upstream cfg_args without executing its Namespace expression."""
    node = ast.parse(Path(path).read_text(), mode="eval").body
    if not (isinstance(node, ast.Call) and isinstance(node.func, ast.Name)
            and node.func.id == "Namespace" and not node.args):
        raise ValueError("cfg_args must contain a literal Namespace(key=value, ...) expression")
    result = {}
    for keyword in node.keywords:
        if keyword.arg is None or keyword.arg in result:
            raise ValueError("cfg_args contains unpacking or duplicate keys")
        result[keyword.arg] = ast.literal_eval(keyword.value)
    return result


def load_file_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


def read_scene_config(path):
    if path.suffix == ".json":
        return json.loads(path.read_text())
    # Use the training ecosystem's own inheritance/merge semantics.
    try:
        from mmcv import Config
    except ImportError:
        try:
            from mmengine.config import Config
        except ImportError as error:
            raise RuntimeError("Python configs require mmcv.Config (as in upstream) or mmengine.Config") from error
    return dict(Config.fromfile(str(path)))


def resolve_model(repo, model=None, cfg_args=None):
    """Select a scene from cfg_args; retain explicit model paths for relocation."""
    if cfg_args is None:
        candidate = (model / "cfg_args") if model is not None else Path.cwd() / "cfg_args"
        if candidate.is_file():
            cfg_args = candidate.resolve()
    elif not cfg_args.is_file():
        raise FileNotFoundError(cfg_args)
    if model is None:
        if cfg_args is None:
            raise ValueError("Supply --cfg-args /path/to/scene/cfg_args, or run from its directory")
        stored = read_namespace(cfg_args).get("model_path")
        if not isinstance(stored, str) or not stored.strip():
            raise ValueError("cfg_args has no model_path; use --model to specify the checkpoint location")
        model = Path(stored).expanduser()
        # Training runs use the reference repository as their working directory.
        if not model.is_absolute():
            model = repo / model
        model = model.resolve()
    if not model.is_dir():
        raise ValueError("Saved model directory does not exist: %s; use --model for a relocated checkpoint" % model)
    return model, cfg_args


def resolve_config(repo, model, config_path, overrides, saved_args=None):
    arguments = load_file_module("_four_dgs_export_arguments", repo / "arguments/__init__.py")
    parser = argparse.ArgumentParser(add_help=False)
    arguments.ModelHiddenParams(parser)
    hidden = vars(parser.parse_args([]))
    origin = {key: "repository default" for key in hidden}
    sources = [{"kind": "defaults", "path": str(repo / "arguments/__init__.py"),
                "sha256": sha256(repo / "arguments/__init__.py")}]
    saved = saved_args if saved_args is not None else model / "cfg_args"
    layers = []
    if saved.is_file():
        layers.append(("cfg_args", read_namespace(saved), False))
        sources.append({"kind": "cfg_args", "path": str(saved), "sha256": sha256(saved)})
    ignored = []
    if config_path is not None:
        scene = read_scene_config(config_path)
        if not isinstance(scene.get("ModelHiddenParams"), dict):
            raise ValueError("Config must contain a ModelHiddenParams mapping (possibly inherited)")
        # Upstream merge_hparams ignores config keys absent from its argument parser.
        # Some official configs contain obsolete keys (e.g. weight_decay_iteration).
        ignored = sorted(set(scene["ModelHiddenParams"]) - set(hidden))
        layers.append(("scene config", scene["ModelHiddenParams"], False))
        sources.append({"kind": "config", "path": str(config_path), "sha256": sha256(config_path)})
    explicit = {}
    for override in overrides:
        key, separator, value = override.partition("=")
        if not separator:
            raise ValueError("--set requires KEY=JSON_VALUE")
        explicit[key] = json.loads(value)
    layers.append(("explicit override", explicit, True))
    for label, values, reject_unknown in layers:
        for key, value in values.items():
            if key not in hidden:
                if reject_unknown:
                    raise ValueError("Unknown ModelHiddenParams key: " + key)
                continue
            hidden[key], origin[key] = value, label
    # A resolved snapshot is sufficient to reproduce inherited configuration values.
    hidden = json.loads(json.dumps(hidden))
    validate_config(hidden)
    return hidden, {"sources": sources, "value_origins": origin, "ignored_scene_config_keys": ignored,
                    "precedence": ["repository defaults", "cfg_args", "scene config", "--set"]}


def validate_config(config):
    for key in ("no_grid", "static_mlp", "empty_voxel"):
        if config.get(key) is not False:
            raise ValueError("Export requires " + key + "=false")
    if config.get("grid_pe") != 0:
        raise ValueError("Export requires grid_pe=0")
    for key in ("no_dx", "no_ds", "no_dr", "no_do", "no_dshs", "apply_rotation"):
        if type(config.get(key)) is not bool:
            raise ValueError(key + " must be a JSON boolean")
    plane = config["kplanes_config"]
    if plane["grid_dimensions"] != 2 or plane["input_coordinate_dim"] != 4:
        raise ValueError("Export requires six 2D planes over x,y,z,t")
    dimensions = list(plane["resolution"]) + [plane["output_coordinate_dim"], config["net_width"]]
    if len(plane["resolution"]) != 4 or any(type(v) is not int or v < 2 for v in dimensions):
        raise ValueError("Plane dimensions, channel count and network width must be integers >= 2")
    if not config["multires"] or any(type(v) is not int or v < 1 for v in config["multires"]):
        raise ValueError("multires must contain positive integers")
    if type(config["defor_depth"]) is not int or config["defor_depth"] < 0:
        raise ValueError("defor_depth must be a nonnegative integer")


def find_checkpoint(model, iteration):
    if (model / "deformation.pth").is_file():
        if iteration != -1:
            raise ValueError("For a direct checkpoint directory omit --iteration")
        checkpoint = model
    else:
        root = model / "point_cloud"
        if iteration == -1:
            candidates = [int(p.name[len("iteration_"):]) for p in root.glob("iteration_*")
                          if p.name[len("iteration_"):].isdigit()
                          and (p / "deformation.pth").is_file() and (p / "point_cloud.ply").is_file()]
            if not candidates:
                raise ValueError("No fine-stage iteration checkpoints found in " + str(root))
            iteration = max(candidates)
        checkpoint = root / ("iteration_" + str(iteration))
    for name in ("deformation.pth", "point_cloud.ply"):
        if not (checkpoint / name).is_file():
            raise FileNotFoundError(checkpoint / name)
    return checkpoint


def import_reference(repo):
    # This is a standalone process. Avoid scene/__init__.py, which loads datasets,
    # camera code and CUDA rasterizer extensions unrelated to deformation inference.
    for name in list(sys.modules):
        if name == "scene" or name.startswith("scene.") or name == "utils" or name.startswith("utils."):
            del sys.modules[name]
    sys.path.insert(0, str(repo))
    for name in ("scene", "utils"):
        package = types.ModuleType(name)
        package.__path__ = [str(repo / name)]
        sys.modules[name] = package
    return importlib.import_module("scene.deformation").deform_network


def repository_provenance(repo):
    def git(*args):
        result = subprocess.run(["git", "-C", str(repo), *args], capture_output=True, text=True)
        return result.stdout.strip() if result.returncode == 0 else None
    files = ("scene/deformation.py", "scene/hexplane.py", "scene/grid.py", "utils/graphics_utils.py")
    return {"path": str(repo), "revision": git("rev-parse", "HEAD"),
            "tracked_changes": git("status", "--porcelain", "--untracked-files=no"),
            "source_sha256": {name: sha256(repo / name) for name in files}}
