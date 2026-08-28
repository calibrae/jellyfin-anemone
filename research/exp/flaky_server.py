#!/usr/bin/env python3
"""Serves files/<path> but drops the connection after sending DROP_AFTER bytes,
on the first request only (per path), to test -reconnect behavior.
Second request (with Range header resuming) is served fully.
"""
import http.server, socketserver, os, json, time, threading

PORT = int(os.environ.get("PORT", "8002"))
DROP_AFTER = int(os.environ.get("DROP_AFTER", "500000"))
LOGFILE = os.environ.get("LOGFILE", "flaky.log")
lock = threading.Lock()
seen = {}

class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    def log_message(self, fmt, *args):
        pass
    def do_GET(self):
        local = os.path.join("files", self.path.lstrip("/"))
        rng = self.headers.get("Range")
        rec = {"t": time.time(), "path": self.path, "range": rng}
        with lock:
            with open(LOGFILE, "a") as f:
                f.write(json.dumps(rec) + "\n")
        print(json.dumps(rec), flush=True)
        if not os.path.isfile(local):
            self.send_response(404); self.end_headers(); return
        size = os.path.getsize(local)
        start, end = 0, size - 1
        status = 200
        if rng and rng.startswith("bytes="):
            r = rng[6:].split("-")
            if r[0]: start = int(r[0])
            if len(r) > 1 and r[1]: end = int(r[1])
            status = 206
        self.send_response(status)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Length", str(end - start + 1))
        if status == 206:
            self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
        self.end_headers()
        with lock:
            key = self.path
            first_time = key not in seen
            seen[key] = seen.get(key, 0) + 1
        with open(local, "rb") as fh:
            fh.seek(start)
            remaining = end - start + 1
            sent = 0
            while remaining > 0:
                chunk = fh.read(min(65536, remaining))
                if not chunk:
                    break
                try:
                    self.wfile.write(chunk)
                except (BrokenPipeError, ConnectionResetError):
                    return
                sent += len(chunk)
                remaining -= len(chunk)
                if first_time and sent >= DROP_AFTER:
                    # simulate network drop: abruptly close the socket
                    print(json.dumps({"t": time.time(), "action": "DROP", "path": self.path, "sent": sent}), flush=True)
                    try:
                        self.connection.shutdown(1)
                        self.connection.close()
                    except OSError:
                        pass
                    return

class ThreadingHTTPServer(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True

if __name__ == "__main__":
    open(LOGFILE, "w").close()
    srv = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    print(f"listening on {PORT}, dropping after {DROP_AFTER} bytes on first request per path", flush=True)
    srv.serve_forever()
