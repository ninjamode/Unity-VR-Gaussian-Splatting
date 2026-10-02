# Tests

The package contains a small EditMode suite focused on 3DGS. It creates a deterministic 1,000-splat binary PLY and temporary rendering assets during the run, then deletes them, without requiring external tools or connections. It should stay that way.

Coverage includes PLY attribute/SH conversion and truncated input, 16/24/32-bit sorting, visibility compaction, independent stereo matrices, full/indirect drawing parity and a few benchmark path/scheduling/statistics regressions. Cameras and render targets use explicit matrices and dimensions. GPU rendering tests require compute shader support.

To enable package tests, install Unity Test Framework and add this to your project's `Packages/manifest.json`:

```json
"testables": ["net.kleinbeck.gaussians"]
```

Run `Gaussians.Package.Tests` in the EditMode Test Runner. The renderer fixture imports its generated assets once and gives each case a fresh renderer.
