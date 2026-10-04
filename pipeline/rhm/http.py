"""Polite HTTP: per-host rate limiting, retry with backoff, on-disk cache."""

from __future__ import annotations

import hashlib
import json
import os
import time
from pathlib import Path
from urllib.parse import urlparse

import requests

from .config import MIN_INTERVAL, USER_AGENT

CACHE_DIR = Path(os.environ.get("RHM_CACHE", Path(__file__).resolve().parents[1] / ".cache"))


class PoliteSession:
    def __init__(self, cache: bool = True, max_retries: int = 4):
        self.s = requests.Session()
        self.s.headers["User-Agent"] = USER_AGENT
        self.cache = cache
        self.max_retries = max_retries
        self._last: dict[str, float] = {}
        CACHE_DIR.mkdir(parents=True, exist_ok=True)

    def _wait(self, host: str) -> None:
        key = next((k for k in MIN_INTERVAL if host.endswith(k)), "default")
        gap = MIN_INTERVAL[key]
        dt = time.monotonic() - self._last.get(host, 0)
        if dt < gap:
            time.sleep(gap - dt)
        self._last[host] = time.monotonic()

    def _cache_path(self, url: str, headers: dict | None) -> Path:
        h = hashlib.sha1((url + json.dumps(headers or {}, sort_keys=True)).encode()).hexdigest()
        return CACHE_DIR / h[:2] / h

    def get(self, url: str, headers: dict | None = None, allow_404: bool = True) -> tuple[int, str]:
        """Return (status, body). 404 bodies are cached too so reruns stay cheap."""
        cp = self._cache_path(url, {k: v for k, v in (headers or {}).items() if k.lower() != "authorization"})
        if self.cache and cp.exists():
            status, _, body = cp.read_text(encoding="utf-8").partition("\n")
            return int(status), body

        host = urlparse(url).netloc
        delay = 2.0
        for attempt in range(self.max_retries + 1):
            self._wait(host)
            try:
                r = self.s.get(url, headers=headers, timeout=30)
            except requests.RequestException:
                if attempt == self.max_retries:
                    raise
                time.sleep(delay); delay *= 2
                continue
            if r.status_code == 403 and r.headers.get("X-RateLimit-Remaining") == "0":
                reset = int(r.headers.get("X-RateLimit-Reset", time.time() + 60))
                time.sleep(max(1, reset - time.time()) + 1)
                continue
            if r.status_code in (429, 500, 502, 503, 504):
                retry_after = r.headers.get("Retry-After")
                time.sleep(float(retry_after) if retry_after and retry_after.isdigit() else delay)
                delay *= 2
                continue
            break
        if r.status_code >= 400 and not (allow_404 and r.status_code == 404):
            r.raise_for_status()
        if self.cache and r.status_code in (200, 404):
            cp.parent.mkdir(parents=True, exist_ok=True)
            cp.write_text(f"{r.status_code}\n{r.text}", encoding="utf-8")
        return r.status_code, r.text

    def get_json(self, url: str, headers: dict | None = None):
        status, body = self.get(url, headers=headers)
        return json.loads(body) if status == 200 else None
