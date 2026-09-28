#!/usr/bin/env python3
"""Dependency-free local preview server with history-route fallback."""

from argparse import ArgumentParser
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path(__file__).resolve().parent


class WebsiteHandler(SimpleHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=str(ROOT), **kwargs)

    def translate_path(self, path):
        translated = Path(super().translate_path(path))
        if translated.is_file():
            return str(translated)
        return str(ROOT / "index.html")

    def log_message(self, format, *args):
        print("[%s] %s" % (self.log_date_time_string(), format % args))


if __name__ == "__main__":
    parser = ArgumentParser(description="Preview the Veyon Campus website locally")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=4173)
    args = parser.parse_args()
    server = ThreadingHTTPServer((args.host, args.port), WebsiteHandler)
    print("Veyon Campus website preview: http://%s:%s" % (args.host, args.port))
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nPreview server stopped.")
    finally:
        server.server_close()
