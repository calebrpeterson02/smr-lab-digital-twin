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

## Running in a browser (Docker)

`docker-compose.yml` runs the twin as a website on the lab network: nginx serves the Unity WebGL
build and passes its WebSocket connections (`/ws/robot`, `/ws/imu`) to the bridges, which read
from Postgres. Viewers only need a browser.

1. **Database connection.** Create `vt_middleware/.env` (the same file the bridges use when run
   directly) with the database's address and login:
   ```
   DB_HOST=192.168.1.104
   DB_PORT=5433
   DB_USER=...
   DB_PASSWORD=...
   DB_NAME=manufacturing_db
   ```
2. **WebGL build.** In Unity, on a machine that has the robot meshes:
   - File > Build Profiles > Web, then Switch Platform.
   - Player Settings > Publishing Settings: set Compression Format to **Gzip**. The site is
     served over http, and browsers only accept Brotli over https.
   - Build into `deploy/webgl/` (gitignored: the build contains the licensed meshes).
3. **Start it** from the repo root:
   ```bash
   docker compose up -d --build
   ```
   Then open `http://<host>/`. If port 80 is taken, run with `WEB_PORT=8080`, for example.

`docker compose logs -f` shows the bridge and nginx logs; `docker compose down` stops everything.
After a new WebGL build, just reload the page. After changing the bridge code, run
`docker compose up -d --build` again.
