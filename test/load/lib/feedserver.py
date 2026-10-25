"""Tiny RSS origin for load tests: GET /feed/<n> returns 20 items; every fetch adds one new item."""
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

counts = {}
lock = threading.Lock()


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def do_GET(self):
        with lock:
            n = counts.get(self.path, 0) + 1
            counts[self.path] = n
        items = "".join(
            f"<item><title>Post {i} of {self.path}</title>"
            f"<link>http://example.invalid{self.path}/post/{i}</link>"
            f"<guid>{self.path}#{i}</guid>"
            f"<pubDate>Fri, 24 Oct 2026 {(i // 60) % 24:02d}:{i % 60:02d}:00 GMT</pubDate>"
            f"<description>Body of post {i} for feed {self.path}, a few sentences of text to parse like a real entry would carry.</description></item>"
            for i in range(n, n + 20)
        )
        body = (f'<?xml version="1.0"?><rss version="2.0"><channel><title>Feed {self.path}</title>'
                f'<link>http://example.invalid{self.path}</link><description>synthetic</description>{items}</channel></rss>').encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/rss+xml")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


ThreadingHTTPServer(("0.0.0.0", 80), Handler).serve_forever()
