# Image for the Postgres -> WebSocket bridges (robot on 8765, IMU on 8766).
# Built and started by docker-compose.yml in the repo root; DB_DSN comes from
# the environment there, not from a .env file in the image.
FROM python:3.12-slim

# Print log lines straight away, so `docker compose logs` keeps up
ENV PYTHONUNBUFFERED=1

WORKDIR /app

# Dependencies first: this layer is reused until requirements.txt changes
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY *.py ./

# The bridges need no privileges, so don't run them as root
USER nobody

# Ports are reached by nginx over the compose network, not published on the host
EXPOSE 8765 8766

# Run directly (not via a shell) so `docker stop`'s SIGTERM reaches Python,
# which closes every viewer cleanly - see serve() in bridge_core.py
CMD ["python", "run_all.py"]
