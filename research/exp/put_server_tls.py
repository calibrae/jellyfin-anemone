#!/usr/bin/env python3
"""Same as put_server.py but wrapped in TLS with a self-signed cert."""
import http.server, socketserver, ssl, os, json, time, threading

PORT = int(os.environ.get("PORT", "8443"))
LOGFILE = os.environ.get("LOGFILE", "servertls.log")
lock = threading.Lock()

class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    def log_message(self, fmt, *args):
        pass
    def do_PUT(self):
        length = self.headers.get("Content-Length")
        te = self.headers.get("Transfer-Encoding")
        nbytes = 0
        if te and te.lower() == "chunked":
            while True:
                line = self.rfile.readline().strip()
                if not line: break
                size = int(line.split(b";")[0], 16)
                if size == 0:
                    self.rfile.readline(); break
                nbytes += len(self.rfile.read(size))
                self.rfile.readline()
        elif length is not None:
            nbytes = len(self.rfile.read(int(length)))
        rec = {"t": time.time(), "method": "PUT", "path": self.path, "bytes": nbytes}
        with lock:
            with open(LOGFILE, "a") as f:
                f.write(json.dumps(rec) + "\n")
        print(json.dumps(rec), flush=True)
        self.send_response(200)
        self.send_header("Content-Length", "0")
        self.end_headers()

class ThreadingHTTPServer(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True

if __name__ == "__main__":
    open(LOGFILE, "w").close()
    srv = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ctx.load_cert_chain(certfile="cert.pem", keyfile="key.pem")
    srv.socket = ctx.wrap_socket(srv.socket, server_side=True)
    print(f"listening TLS on {PORT}", flush=True)
    srv.serve_forever()
