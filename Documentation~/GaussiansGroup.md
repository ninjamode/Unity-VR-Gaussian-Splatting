# GaussiansGroup

Add **Gaussians > Gaussians Group** to a GameObject. Drag 3DGS renderer components into Members, or click **Add Hierarchy** to add every 3DGS component on that GameObject and its descendants, including inactive children.

A renderer can belong to only one group. Adding an already-owned renderer fails with a Console warning naming its owner. A renderer component also displays its group. Disabling the group releases its GPU resources and lets members render independently; membership remains assigned.

## Shared Settings Override

The group overrides alpha cutoff, opacity-aware bounds, render path, projection, render order, sort precision/cadence, compaction policy, write depth, color conversion and shader resources. Member values are preserved and restored when the group is disabled or membership is removed, the renderer Inspector displays active overrides read-only. Differing member settings do not split the group.

Active groups use Splats display mode, overriding member debug modes. There is no nested-group support. Missing or unsupported group shaders are reported as errors and prevent group rendering, without an independent fallback. Disabled renderers, invalid assets and camera layer exclusions prevent individual members from participating.

## Clipping

Group Cutouts accepts existing GaussianCutout boxes or ellipsoids. Each member evaluates its own cutouts first, followed by group cutouts, with duplicate references removed.
