# SMR Lab Digital Twin

Unity digital twin of a FANUC R-2000iC/165F robot arm, driven by robot telemetry and arm-mounted IMUs.

- `vt_middleware/`: Python WebSocket bridges that read from the lab's Postgres database and stream to Unity.
- `unity/`: the Unity project.

## Robot model (not included)

The robot meshes are not in this repository. They are derived from the
[FANUC R-2000iA/165F model on GrabCAD](https://grabcad.com/library/fanuc-r2000ia-165f), whose
terms allow only internal, non-commercial use, so they can't be published here.

To run the Unity project, place `robotarm_colored.fbx` in `unity/Assets/Model/`. Lab members can
get it from the project maintainer. It is a Blender conversion of the GrabCAD files, and
`robotarm_colored.prefab` references its parts by name, so a fresh conversion from GrabCAD won't
line up without matching that hierarchy. The `.fbx.meta` files are tracked, so Unity reconnects
the prefab once the file is in place.

The WebGL build embeds the meshes, so it must only be served on the lab network.
