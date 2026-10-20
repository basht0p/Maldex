"""Maldex mitmproxy addon: append one JSON line per completed/failed flow while capture is enabled.

Options:
  maldex_out          path of the JSONL file to append to
  maldex_enable_file  capture happens only while this file exists

Never raises: a capture problem must not disturb the proxied connection.
"""
import base64
import json
import os
import time

from mitmproxy import ctx

BODY_CAP = 1024 * 1024


def _body(msg):
    try:
        raw = msg.raw_content if msg is not None else None
    except Exception:
        raw = None
    if not raw:
        return "", False
    truncated = len(raw) > BODY_CAP
    return base64.b64encode(raw[:BODY_CAP]).decode("ascii"), truncated


def _headers(msg):
    try:
        return [[k.decode("utf-8", "replace"), v.decode("utf-8", "replace")] for k, v in msg.headers.fields]
    except Exception:
        return []


class MaldexJsonl:
    def load(self, loader):
        loader.add_option("maldex_out", str, "", "JSONL output path")
        loader.add_option("maldex_enable_file", str, "", "Capture only while this file exists")

    def _write(self, flow, failed):
        try:
            enable = ctx.options.maldex_enable_file
            out = ctx.options.maldex_out
            if not enable or not out or not os.path.exists(enable):
                return
            req = flow.request
            resp = flow.response
            req_b64, req_trunc = _body(req)
            resp_b64, resp_trunc = _body(resp)
            rec = {
                "ts": time.time(),
                "method": req.method,
                "url": req.pretty_url,
                "host": req.pretty_host,
                "status": resp.status_code if resp is not None else None,
                "req_headers": _headers(req),
                "resp_headers": _headers(resp) if resp is not None else [],
                "req_body_b64": req_b64,
                "resp_body_b64": resp_b64,
                "tls": req.scheme == "https",
                "truncated": bool(req_trunc or resp_trunc),
            }
            if failed:
                rec["error"] = str(flow.error.msg) if flow.error else "error"
            with open(out, "a", encoding="utf-8") as fh:
                fh.write(json.dumps(rec) + "\n")
        except Exception:
            pass

    def response(self, flow):
        self._write(flow, False)

    def error(self, flow):
        self._write(flow, True)


addons = [MaldexJsonl()]
