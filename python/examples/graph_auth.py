"""Development sign-in for the Microsoft Graph connector (docs/connectors/graph.md). Not part of the topwater package:
signing in is the host application's job, and a real one uses its own identity library (MSAL, Azure.Identity). This file
is what the examples and the live tests use against a test tenant.

    python examples/graph_auth.py sign-in      # device code: prints a code to enter at microsoft.com/devicelogin

Settings come from ~/.config/topwater/graph.env (GRAPH_TENANT_ID, GRAPH_CLIENT_ID, GRAPH_SITE_URL) or the environment.
The refresh token is kept in ~/.config/topwater/graph.token.json (mode 0600) and rewritten every time Microsoft
rotates it. Nothing here prints or logs a token.
"""
from __future__ import annotations

import json
import os
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

CONFIG = Path.home() / ".config" / "topwater"
ENV_FILE = CONFIG / "graph.env"
TOKEN_FILE = CONFIG / "graph.token.json"
SCOPE = "https://graph.microsoft.com/.default offline_access"


def settings() -> dict | None:
    """GRAPH_TENANT_ID, GRAPH_CLIENT_ID and GRAPH_SITE_URL from the environment or graph.env; None when incomplete."""
    out = {}
    if ENV_FILE.is_file():
        for line in ENV_FILE.read_text().splitlines():
            k, sep, v = line.partition("=")
            if sep and not k.strip().startswith("#"):
                out[k.strip()] = v.strip().strip('"')
    for k in ("GRAPH_TENANT_ID", "GRAPH_CLIENT_ID", "GRAPH_SITE_URL"):
        if os.environ.get(k):
            out[k] = os.environ[k]
    return out if all(out.get(k) for k in ("GRAPH_TENANT_ID", "GRAPH_CLIENT_ID", "GRAPH_SITE_URL")) else None


def _post(url: str, form: dict) -> dict:
    req = urllib.request.Request(url, urllib.parse.urlencode(form).encode(), {"Content-Type": "application/x-www-form-urlencoded"})
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            return json.loads(r.read())
    except urllib.error.HTTPError as e:
        body = json.loads(e.read() or b"{}")
        if body.get("error") in ("authorization_pending", "slow_down"):
            return body
        raise RuntimeError(f"sign-in failed: {body.get('error')}: {str(body.get('error_description', ''))[:300]}") from None


def _save(refresh_token: str, path: Path = TOKEN_FILE) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".tmp")
    fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w") as f:
        json.dump({"refresh_token": refresh_token}, f)
    os.chmod(tmp, 0o600)
    os.replace(tmp, path)


class RefreshTokenProvider:
    """A token provider for GraphSource: call it for the current access token. It redeems the saved refresh token, keeps
    the access token until five minutes before it expires, and saves the rotated refresh token."""

    def __init__(self, tenant: str, client_id: str, token_file: Path = TOKEN_FILE):
        self._url = f"https://login.microsoftonline.com/{urllib.parse.quote(tenant)}/oauth2/v2.0/token"
        self._client, self._file = client_id, Path(token_file)
        self._token, self._expires = "", 0.0
        self._lock = threading.Lock()

    def __call__(self) -> str:
        with self._lock:
            if time.time() < self._expires - 300:
                return self._token
            saved = json.loads(self._file.read_text())["refresh_token"]
            got = _post(self._url, {"grant_type": "refresh_token", "client_id": self._client, "refresh_token": saved, "scope": SCOPE})
            if "access_token" not in got:
                raise RuntimeError(f"sign-in failed: {got.get('error')}")
            if got.get("refresh_token"):
                _save(got["refresh_token"], self._file)
            self._token, self._expires = got["access_token"], time.time() + float(got.get("expires_in", 3600))
            return self._token


def provider() -> RefreshTokenProvider | None:
    s = settings()
    return RefreshTokenProvider(s["GRAPH_TENANT_ID"], s["GRAPH_CLIENT_ID"]) if s and TOKEN_FILE.is_file() else None


def device_code_sign_in(tenant: str, client_id: str) -> None:
    """Signs in with a device code (the app registration must allow public client flows) and saves the refresh token."""
    base = f"https://login.microsoftonline.com/{urllib.parse.quote(tenant)}/oauth2/v2.0"
    start = _post(base + "/devicecode", {"client_id": client_id, "scope": SCOPE})
    print(start.get("message") or f"Go to {start['verification_uri']} and enter {start['user_code']}")
    interval = int(start.get("interval", 5))
    deadline = time.time() + int(start.get("expires_in", 900))
    while time.time() < deadline:
        time.sleep(interval)
        got = _post(base + "/token", {"grant_type": "urn:ietf:params:oauth:grant-type:device_code", "client_id": client_id,
                                      "device_code": start["device_code"]})
        if got.get("error") == "slow_down":
            interval += 5
        elif "refresh_token" in got:
            _save(got["refresh_token"])
            print(f"signed in; refresh token saved to {TOKEN_FILE}")
            return
    raise RuntimeError("sign-in timed out")


if __name__ == "__main__":
    s = settings()
    if not s:
        sys.exit(f"set GRAPH_TENANT_ID, GRAPH_CLIENT_ID and GRAPH_SITE_URL in {ENV_FILE} or the environment")
    if sys.argv[1:] == ["sign-in"]:
        device_code_sign_in(s["GRAPH_TENANT_ID"], s["GRAPH_CLIENT_ID"])
    else:
        sys.exit("usage: graph_auth.py sign-in")
