#!/usr/bin/env python3
"""Minimal HTTP server that logs PUT/GET/DELETE requests (method, path, headers, body size)
one JSON line per request to stdout (line-buffered) and to a log file.
Supports:
  - PORT env var (default 8000)
  - FAIL_PATHS env var: comma-separated substrings; any PUT whose path contains one of these
    substrings gets a 500 response (to test ffmpeg's error handling / -ignore_io_errors).
  - FAIL_COUNT env var: how many times to fail matching paths before succeeding (default: always fail).
"""
import http.server
import socketserver
import sys
import os
import json
import time
import threading

PORT = int(os.environ.get("PORT", "8000"))
FAIL_PATHS = [p for p in os.environ.get("FAIL_PATHS", "").split(",") if p]
FAIL_COUNT = int(os.environ.get("FAIL_COUNT", "0"))  # 0 = fail forever
LOGFILE = os.environ.get("LOGFILE", "server.log")

fail_counters = {}
lock = threading.Lock()

class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"  # enable keep-alive so we can observe persistent connections

    def log_message(self, fmt, *args):
        pass  # silence default stderr logging; we do our own

    def _record(self, method):
        length = self.headers.get("Content-Length")
        te = self.headers.get("Transfer-Encoding")
        body = b""
        nbytes = 0
        if te and te.lower() == "chunked":
            # read chunked body manually
            while True:
                line = self.rfile.readline().strip()
                if not line:
                    break
                try:
                    size = int(line.split(b";")[0], 16)
                except ValueError:
                    break
                if size == 0:
                    self.rfile.readline()
                    break
                chunk = self.rfile.read(size)
                nbytes += len(chunk)
                self.rfile.readline()  # trailing CRLF
        elif length is not None:
            n = int(length)
            body = self.rfile.read(n)
            nbytes = len(body)

        rec = {
            "t": time.time(),
            "method": method,
            "path": self.path,
            "content_length_hdr": length,
            "transfer_encoding": te,
            "actual_body_bytes": nbytes,
            "connection_hdr": self.headers.get("Connection"),
            "expect_hdr": self.headers.get("Expect"),
            "range_hdr": self.headers.get("Range"),
            "headers_all": dict(self.headers.items()),
            "client_conn_id": id(self.connection),
            "client_port": self.client_address[1],
        }
        with lock:
            with open(LOGFILE, "a") as f:
                f.write(json.dumps(rec) + "\n")
        print(json.dumps({k: rec[k] for k in ("method","path","content_length_hdr","transfer_encoding","connection_hdr","range_hdr")}), flush=True)
        return rec

    def do_PUT(self):
        rec = self._record("PUT")
        path = self.path
        should_fail = False
        with lock:
            for fp in FAIL_PATHS:
                if fp in path:
                    cnt = fail_counters.get(fp, 0)
                    if FAIL_COUNT == 0 or cnt < FAIL_COUNT:
                        should_fail = True
                        fail_counters[fp] = cnt + 1
                    break
        if should_fail:
            self.send_response(500)
            self.send_header("Content-Length", "0")
            self.end_headers()
        else:
            self.send_response(200)
            self.send_header("Content-Length", "0")
            self.end_headers()

    def do_GET(self):
        rec = self._record("GET")
        # serve a file if it exists under ./files/<path>, else 404
        local = os.path.join("files", self.path.lstrip("/"))
        if os.path.isfile(local):
            self.send_response(200)
            size = os.path.getsize(local)
            rng = self.headers.get("Range")
            start, end = 0, size - 1
            status = 200
            if rng and rng.startswith("bytes="):
                r = rng[6:].split("-")
                if r[0]:
                    start = int(r[0])
                if len(r) > 1 and r[1]:
                    end = int(r[1])
                status = 206
            self.send_response(status)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Accept-Ranges", "bytes")
            self.send_header("Content-Length", str(end - start + 1))
            if status == 206:
                self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
            self.end_headers()
            with open(local, "rb") as fh:
                fh.seek(start)
                self.wfile.write(fh.read(end - start + 1))
        else:
            self.send_response(404)
            self.send_header("Content-Length", "0")
            self.end_headers()

    def do_DELETE(self):
        self._record("DELETE")
        self.send_response(200)
        self.send_header("Content-Length", "0")
        self.end_headers()

    def do_POST(self):
        self._record("POST")
        self.send_response(200)
        self.send_header("Content-Length", "0")
        self.end_headers()

class ThreadingHTTPServer(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True

if __name__ == "__main__":
    open(LOGFILE, "w").close()
    srv = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    print(f"listening on {PORT}, logfile={LOGFILE}", flush=True)
    srv.serve_forever()
