# Gaussian benchmarks

Compare renderer settings with repeatable camera poses and model inputs. A benchmark scene supplies its own models and render-pipeline integration; no example assets are required.

## Quick start

1. Open a dedicated scene with working Gaussian rendering. Use **Tools → Gaussians → Benchmark → Create Experiment** to create an experiment, config asset, camera, viewpoint and subject root.
2. Add your renderers and imported models beneath the experiment. Assign subject roots to **Variants**. The same root can appear in multiple variants to compare settings.
3. Position the viewpoint objects, or use **Capture Scene View as Camera Point**. Choose **Fixed Views** for separate trials per viewpoint, or **Path** to traverse all points during each trial.
4. Set warm-up frames, measured frames, repetitions and run mode in the config. Use **Show Pose in Game View** to inspect a variant and camera sample, then **Validate Scene** from the benchmark menu.
5. Save the scene. Use **Enter Play Mode and Run This Experiment** for a functional check, or **Build Current Scene** / **Build and Run Current Scene** for player measurements.

The player starts enabled experiments automatically and exits after writing results. The build commands include only the active scene and temporarily enable frame timing statistics. Keep benchmark scenes out of your normal Build Profiles.

## Settings and consistency

Variants select a render path and optional renderer overrides. Unchecked overrides inherit the subject's authored values. An assigned **Shared Settings** asset replaces inline settings entirely; create one with **Assets → Create → Gaussians → Benchmark Renderer Settings**. Use explicit settings when comparing different subjects.

For 3D renderers, benchmarks use explicit optimization controls: compaction and deferred SH loading default to off. Enable their overrides to compare them, or apply **Compaction Threshold** to use the renderer's automatic policy.

The camera controls projection and clipping planes. The config controls desktop resolution and a fixed simulation step. Viewpoints record position and rotation; model alignment stays on subject transforms. **Show Pose in Game View** edits scene objects and supports Undo. Match the Game View resolution to the config when checking framing.

Each trial uses a fresh clone of the experiment. Warm-up traverses the configured sequence, then measurement restarts at sample zero. Path segments receive equal portions of each phase. Source state and global frame settings are restored after a run or cancellation; construction and result writing are outside measured intervals.

Disable scripts, physics, automatic animation and adaptive resolution that change benchmark inputs. Scripts outside the experiment are not reset. Use Release players for timing comparisons; Editor measurements include Editor overhead. Increase sample counts and repetitions for performance conclusions.

Optional scheduling controls shuffle experiment/variant order with a fixed seed and keep explicitly grouped Direct/Composite pairs adjacent. Configs in one scene must agree on shared scheduling settings. `schedule.csv` records the intended trial order.

## Desktop and XR

- **Desktop Throughput** disables VSync and frame limiting, and applies the config's window resolution.
- **XR Frame Budget** preserves headset pacing and uses the XR runtime's eye resolution. Configure an XR provider and the renderer's XR integration before building. Trials reject missing stereo rendering, loss of focus/display, changing resolution, or unmet configured refresh/timing requirements.

XR trials use the authored center-camera path by default; disable Tracked Pose Drivers and other camera-motion scripts for that policy. To move a rig while retaining a tracked child camera, assign **Camera Path Root** to an ancestor of the benchmark camera inside the experiment. Viewpoints then drive the root, while head tracking drives the child. The runtime still supplies eye offsets, projections and reprojection; keep the headset stationary for comparisons. Paced FPS does not measure uncapped throughput. Compare device GPU timings with the recorded headset frame budget.

Relative output folders are beneath `Application.persistentDataPath`; the player logs the full path. Desktop players also accept `--gaussians-benchmark-output /absolute/results/directory`. On Android, use a directly installable APK and retrieve results with your Player Settings package name:

```sh
adb pull "/sdcard/Android/data/<package-name>/files/GaussianBenchmarks" ./benchmark-results
```

## 4DGS and extensions

For 4D experiments, attach **Gaussians → Benchmark → 4D Controls** to the experiment root so benchmark samples control animation. Entries match variant IDs and select canonical, ONNX or Native rendering, with optional runtime SH storage overrides. Import the required backends first; see the [4DGS guide](4DGS.md).

Hold a model time, cycle explicit times, or advance through a range using the config's simulation step. Held poses can reuse cached deformation; use changing times to measure continuous deformation. Import-time canonical SH precision and runtime animated SH storage are independent.

For other controls, derive a component from `GaussianBenchmarkExtension` and attach it to the experiment root. `Configure` sets up the inactive trial subject; `SetSample` applies deterministic inputs; `Describe` adds result metadata. Keep references and mutations inside the experiment hierarchy, and keep sample hooks cheap because their work is measured. Custom shared-asset or global changes require your own restoration.

## Results and diagnostics

- `run.json`: device, graphics API, pipeline, build identity, settings, trial status and failure reasons.
- `schedule.csv`: intended execution order, including timing and diagnostic phases.
- `samples.csv`: camera poses, elapsed frame intervals and completed CPU/GPU timing observations.
- `summary.csv`: valid sample counts, median, p95 and p99 per metric, variant, view and repetition.
- `xr-timings.csv`: XR app/compositor GPU observations when available.

CPU/GPU timings arrive asynchronously. Duplicates and a conservative phase boundary are discarded, so valid counts can be lower than elapsed-frame counts. Missing or invalid device timings remain unavailable. Timings cover the whole frame, including other scene content and collection overhead; they do not isolate Gaussian rendering cost.

**Timing Only** collects timings. **Diagnostics Only** captures images and rejection counts; **Timing And Diagnostics** runs diagnostics after all timing trials. Diagnostics require desktop mono rendering with an active 3D renderer, including 4D models rendered through it. PNGs, `images.csv` and `rejections.csv` record sampled output, poses and per-renderer rejection counts. Intermediate path samples still render to preserve sort history. Disable temporal postprocessing for deterministic image comparisons. Diagnostic readback and image encoding are outside timing trials.

A valid config does not prove that the render-pipeline integration drew the model. Inspect the preview and captured output before interpreting results.
