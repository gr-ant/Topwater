"""The test web server for tests/web: serves routes.json over plain HTTP, exactly as the ports' fake transports do.

    python3 server.py routes.json PORT
Every response carries the route's headers, then Content-Length; /echo answers with the request (see README.md). A route
with "raw" sends exactly those bytes (Latin-1) and closes the connection: the malformed responses of the "wire" cases."""
import base64, json, socket, sys, threading

ECHOED = ("content-type", "accept", "authorization", "user-agent", "x-test")


def respond(routes, method, target, headers, body):
    path = target.split("?", 1)[0]
    r = routes.get(path)
    if r is None:
        return 404, "Not Found", [["Content-Type", "text/plain"]], b"no route\n"
    if r.get("echo"):
        lines = [f"{method} {target}"] + [f"{h}: {headers[h]}" for h in ECHOED if h in headers]
        return 200, "OK", [["Content-Type", "text/plain"]], ("\n".join(lines) + "\n\n").encode() + body
    data = base64.b64decode(r["bodyBase64"]) if "bodyBase64" in r else r["body"].encode()
    return r["status"], r["reason"], r["headers"], data


def handle(conn, routes):
    with conn:
        f = conn.makefile("rb")
        while True:
            line = f.readline()
            if not line:
                return
            method, target, _ = line.decode().split(" ", 2)
            headers = {}
            while (h := f.readline().decode()) not in ("\r\n", "\n", ""):
                k, v = h.split(":", 1)
                headers[k.strip().lower()] = v.strip()
            body = f.read(int(headers.get("content-length", "0")))
            raw = routes.get(target.split("?", 1)[0], {}).get("raw")
            if raw is not None:
                conn.sendall(raw.encode("latin-1"))
                conn.shutdown(socket.SHUT_WR)
                return
            status, reason, hs, data = respond(routes, method, target, headers, body)
            out = f"HTTP/1.1 {status} {reason}\r\n" + "".join(f"{k}: {v}\r\n" for k, v in hs) + f"Content-Length: {len(data)}\r\n\r\n"
            conn.sendall(out.encode() + (b"" if method == "HEAD" else data))


def main():
    routes = json.load(open(sys.argv[1]))["routes"]
    s = socket.socket()
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind(("127.0.0.1", int(sys.argv[2])))
    s.listen()
    while True:
        conn, _ = s.accept()
        threading.Thread(target=handle, args=(conn, routes), daemon=True).start()


main()
