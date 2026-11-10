# Shared by the /ws/* locations in default.conf: proxy a WebSocket connection.
proxy_http_version 1.1;
proxy_set_header Upgrade $http_upgrade;
proxy_set_header Connection $connection_upgrade;
proxy_set_header Host $host;
proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;

# nginx drops a connection that's silent for proxy_read_timeout (default 60 s).
# The bridge pings every 20 s, but a long limit costs nothing.
proxy_read_timeout 1h;
proxy_send_timeout 1h;

# Pass data on as it arrives instead of collecting it first
proxy_buffering off;
