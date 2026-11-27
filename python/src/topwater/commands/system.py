"""System identity and packages: whoami, hostname, uname, date, sudo, apt, id, uptime, free, nproc. See docs/architecture.md."""
from __future__ import annotations

import re
from datetime import datetime, timedelta, timezone

from .._spec import COMMANDS
from ..vfs import ShellError, UsageException, utc_now
from . import Args, spec_message, strftime


_UNITS = {"second": 1, "sec": 1, "minute": 60, "min": 60, "hour": 3600, "day": 86400, "week": 604800, "fortnight": 1209600}


# our own summary of what this date supports (not GNU's help text)

_MONTH_ABBR = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"]


_DAY_ABBR = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"]


_ZONE_HOURS = {"utc": 0, "gmt": 0, "ut": 0, "z": 0, "est": -5, "edt": -4, "cst": -6, "cdt": -5, "mst": -7, "mdt": -6, "pst": -8, "pdt": -7}


_MONTHS_RE = r"(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?"


def parse_date(text: str, now: datetime) -> datetime:
    """See _parse_date; a time past what a datetime holds (GNU takes years past 9999) is an invalid date here."""
    try:
        return _parse_date(text, now)
    except (ValueError, OverflowError, OSError):
        raise ShellError(spec_message("date", "invalidDate", value=text)) from None


def _parse_date(text: str, now: datetime) -> datetime:
    """date -d, from GNU date's documented input formats: items in any order, each optional. Calendar dates
    (2026-11-20, 11/20/2026, 20 Nov 2026, Nov 20, 2026, 20261120), times (13:30[:00[.5]], 1pm), zones
    (UTC, Z, EST..., +0100, +01:00), a day of the week, today/now/yesterday/tomorrow, @EPOCH, and relative items
    ("3 days ago", "next week", "+2 hours") applied last. Results are UTC, the simulated machine's zone."""
    bad = ShellError(spec_message("date", "invalidDate", value=text))
    s = text.strip().lower()
    m = re.fullmatch(r"@(-?\d+(?:\.\d+)?)", s)
    if m:
        return datetime.fromtimestamp(float(m.group(1)), timezone.utc)

    year = month = day = zone = weekday = day_shift = None  # zone in minutes east of UTC
    time: timedelta | None = None
    relative: list[tuple[int, str]] = []
    m = None

    def eat(pattern: str) -> bool:
        nonlocal s, m
        m = re.match("(?:" + pattern + ")", s)
        if not m:
            return False
        s = s[m.end():].lstrip(" \t,")
        return True

    def g(n: int) -> int:
        return int(m.group(n))

    def month_of(n: int) -> int:
        return _MONTH_ABBR.index(m.group(n)[:3]) + 1

    def full_year(y: int, digits: int) -> int:
        return y if digits > 2 else 2000 + y if y < 69 else 1900 + y

    def set_date(y, mo, d):
        nonlocal year, month, day
        if day is not None:
            raise bad
        year, month, day = (y if y is not None else year), mo, d

    def set_zone(minutes: int):
        nonlocal zone
        if zone is not None:
            raise bad
        zone = minutes

    after_time = False
    while s:
        # a signed number right after a time of day is its zone ("10:00 +2" is UTC+2), anywhere else it's relative
        if after_time and eat(r"([+-])\s*(\d{1,2}:\d{2}|\d{1,4})\b"):
            after_time = False
            v = m.group(2).replace(":", "")
            minutes = int(v) * 60 if len(v) <= 2 else int(v[:-2]) * 60 + int(v[-2:])
            set_zone(-minutes if m.group(1) == "-" else minutes)
            continue
        was_time = False
        if eat(r"(\d{4})-(\d{1,2})-(\d{1,2})(?:t(?=\d))?"):
            set_date(g(1), g(2), g(3))
        elif eat(r"(\d{1,2})/(\d{1,2})(?:/(\d{2}|\d{4}))?\b"):
            set_date(full_year(g(3), len(m.group(3))) if m.group(3) else None, g(1), g(2))
        elif eat(r"(\d{4})(\d{2})(\d{2})\b"):
            set_date(g(1), g(2), g(3))
        elif eat(r"(\d{1,2}):(\d{2})(?::(\d{2})(?:[.,](\d+))?)?\s*(?:(a|p)\.?m\.?\b)?"):
            if time is not None:
                raise bad
            h = g(1)
            if m.group(5):
                if not 1 <= h <= 12:
                    raise bad
                h = h % 12 + (12 if m.group(5) == "p" else 0)
            frac = float("0." + m.group(4)) if m.group(4) else 0
            if h > 23 or g(2) > 59 or (m.group(3) and g(3) > 60):
                raise bad
            time = timedelta(hours=h, minutes=g(2), seconds=(g(3) if m.group(3) else 0) + frac)
            was_time = True
        elif eat(r"(\d{1,2})\s*(a|p)\.?m\.?\b"):
            if time is not None or not 1 <= g(1) <= 12:
                raise bad
            time = timedelta(hours=g(1) % 12 + (12 if m.group(2) == "p" else 0))
            was_time = True
        elif eat(r"(\d{1,2})(?:st|nd|rd|th)?[\s-]*" + _MONTHS_RE + r"(?:[\s-]+(\d{4})\b)?"):
            set_date(g(3) if m.group(3) else None, month_of(2), g(1))
        elif eat(_MONTHS_RE + r"\s*(\d{1,2})(?:st|nd|rd|th)?\b(?:,?\s*(\d{4})\b)?"):
            set_date(g(3) if m.group(3) else None, month_of(1), g(2))
        elif eat(r"(next|last|this|[+-]?\s*\d+)?\s*(second|sec|minute|min|hour|day|week|fortnight|month|year)s?\b(\s+ago\b)?"):
            word = m.group(1).replace(" ", "") if m.group(1) else "1"
            n = {"next": 1, "last": -1, "this": 0}.get(word)
            n = int(word) if n is None else n
            relative.append((-n if m.group(3) else n, m.group(2)))
        elif eat(r"(utc|gmt|ut|z|[ecmp][sd]t)\b(?:([+-])(\d{1,2})(?::?(\d{2}))?)?"):
            offset = _ZONE_HOURS[m.group(1)] * 60
            if m.group(2):
                offset += (-1 if m.group(2) == "-" else 1) * (g(3) * 60 + (g(4) if m.group(4) else 0))
            set_zone(offset)
        elif eat(r"([+-])(\d{2}):?(\d{2})\b"):
            set_zone((-1 if m.group(1) == "-" else 1) * (g(2) * 60 + g(3)))
        elif eat(r"(sun|mon|tue|wed|thu|fri|sat)[a-z]*\.?\b"):
            if weekday is not None:
                raise bad
            weekday = _DAY_ABBR.index(m.group(1))
        elif eat(r"(today|now|yesterday|tomorrow)\b"):
            if day_shift is not None:
                raise bad
            day_shift = {"yesterday": -1, "tomorrow": 1}.get(m.group(1), 0)
        elif year is None and day is not None and eat(r"(\d{4})\b"):  # "Fri Nov 20 13:30:00 UTC 2026", date's own output
            year = g(1)
        else:
            raise bad
        after_time = was_time

    # absolute items set the date and time; anything not given comes from now, and a date alone means midnight
    absolute = day is not None or time is not None or weekday is not None
    try:
        t = datetime(year or now.year, month or now.month, day or now.day, tzinfo=timezone.utc)
    except ValueError:
        raise bad from None
    t += time if time is not None else timedelta(0) if absolute else timedelta(
        hours=now.hour, minutes=now.minute, seconds=now.second, microseconds=now.microsecond)
    if zone is not None:
        t -= timedelta(minutes=zone)
    if weekday is not None:
        t += timedelta(days=(weekday - (t.weekday() + 1) % 7) % 7)
    if day_shift is not None:
        t += timedelta(days=day_shift)
    for n, unit in relative:
        if unit in ("month", "year"):
            # like GNU date, a day past the end of the month rolls over (Jan 31 + 1 month = Mar 2 or 3)
            y, mo = divmod(t.month - 1 + n * (12 if unit == "year" else 1), 12)
            try:
                t = t.replace(year=t.year + y, month=mo + 1, day=1) + timedelta(days=t.day - 1)
            except (ValueError, OverflowError):
                raise bad from None
        else:
            t += timedelta(seconds=n * _UNITS[unit])
    return t


_ISO_UNITS = ("hours", "minutes", "date", "seconds", "ns")


_ISO_FORMATS = ("%Y-%m-%dT%H%:z", "%Y-%m-%dT%H:%M%:z", "%Y-%m-%d", "%Y-%m-%dT%H:%M:%S%:z", "%Y-%m-%dT%H:%M:%S,%N%:z")


_FREE_MEM = (8014880, 1992344, 3984512, 12288, 180224, 2020800, 5748660)  # kB: total used free shared buffers cache available


_FREE_SWAP = (2097148, 0, 2097148)


_FREE_BINARY, _FREE_DECIMAL = "bkmg\x01\x02", "_\x03\x04\x05\x06\x07"  # option keys by power: --tebi is \x01, --kilo \x03


def _uname(ctx):
    a = Args.parse(ctx.args, COMMANDS["uname"])
    every = a.has("a")
    parts = []
    if every or a.has("s") or not a.flags:
        parts.append("Linux")
    if every or a.has("n"):
        parts.append(ctx.shell.hostname)
    if every or a.has("r"):
        parts.append("6.8.0-generic")
    if every or a.has("v"):
        parts.append("#1 SMP PREEMPT_DYNAMIC")
    if every or a.has("m"):
        parts.append("x86_64")
    if every or a.has("o"):
        parts.append("GNU/Linux")
    ctx.print(" ".join(parts))


def _date(ctx):
    # the simulated machine's clock is UTC (TZ is unset), so output never depends on the host's time zone
    # -I[UNIT] / --iso-8601[=UNIT] take an optional value, so they are read here, before the shared parser
    iso = None
    args = []
    for x in ctx.args:
        unit = "" if x == "--iso-8601" else x[11:] if x.startswith("--iso-8601=") else x[2:] if x.startswith("-I") else None
        if unit is None:
            args.append(x)
            continue
        iso = "date" if unit == "" else next((u for u in _ISO_UNITS if u.startswith(unit)), None)
        if iso is None:
            raise UsageException(spec_message("date", "invalidIso", value=unit))
    a = Args.parse(args, COMMANDS["date"])
    now = datetime.now(timezone.utc)
    if a.value("d") is not None:
        now = parse_date(a.value("d"), now)
    elif a.value("r") is not None:
        now = ctx.fs.stat(ctx.resolve(a.value("r"))).modified.replace(tzinfo=timezone.utc)
    fmt = next((p for p in a.positional if p.startswith("+")), None)
    if iso is not None:
        ctx.print(strftime(now, _ISO_FORMATS[_ISO_UNITS.index(iso)]))
    elif a.has("R"):
        ctx.print(strftime(now, "%a, %d %b %Y %H:%M:%S +0000"))
    else:
        ctx.print(strftime(now, fmt[1:] if fmt else "%a %b %e %H:%M:%S %Z %Y"))


def _sudo(ctx):
    """The agent is a regular user: privilege escalation fails the way it does on a real server."""
    user = ctx.shell.user
    if ctx.args in (["-V"], ["--version"]):
        ctx.print("Sudo version 1.9.15p5")
        return 0
    if not ctx.args:
        ctx.error(spec_message("sudo", "usage"))
        return 1
    if ctx.args[0] in ("-l", "--list"):
        ctx.error(spec_message("sudo", "listDenied", user=user, host=ctx.shell.hostname))
        return 1
    # `sudo apt install x`: the package manager's own answer (nothing can be installed) says more than a refusal
    target = list(ctx.args)
    while target and target[0].startswith("-"):
        target.pop(0)
    if target and target[0] in ("apt", "apt-get", "apt-cache", "dpkg"):
        return ctx.invoke(target)
    ctx.error(spec_message("sudo", "notSudoer", user=user))
    return 1


def _package_manager(ctx):
    """No package sources and no network: package managers say so, with their usual failure status."""
    if ctx.name == "dpkg":
        ctx.error("dpkg: " + spec_message("dpkg", "unavailable"))
        return 2
    ctx.error(spec_message(ctx.name, "unavailable"))
    return 100


def _su(ctx):
    ctx.error(spec_message("su", "authenticationFailure"))
    return 1


def _id(ctx):
    u = ctx.shell.user
    a = Args.parse(ctx.args, COMMANDS["id"])
    ctx.print((u if a.has("n") else "1000") if a.has("u") or a.has("g") else f"uid=1000({u}) gid=1000({u}) groups=1000({u})")


def _uptime(ctx):
    ctx.print("up 3 days, 4 hours, 12 minutes" if "-p" in ctx.args
              else f" {utc_now():%H:%M:%S} up 3 days,  4:12,  1 user,  load average: 0.08, 0.03, 0.01")


def _free(ctx):
    a = Args.parse(ctx.args, COMMANDS["free"])
    units = [c for c in _FREE_BINARY + _FREE_DECIMAL[1:] if a.has(c)]
    if len(units) > 1:  # shortcut: a repeated flag (-m -m) is counted once, real free rejects it too
        raise ShellError(spec_message("free", "multipleUnits"))
    unit = units[0] if units else "k"
    si = a.has("\x0e") or unit in _FREE_DECIMAL
    base = 1000 if si else 1024
    power = _FREE_BINARY.find(unit) if unit in _FREE_BINARY else _FREE_DECIMAL.index(unit)

    def size(kb):
        n = kb * 1024
        if not a.has("h"):
            return str(n // base ** power)
        if n < base:
            return f"{n}B"
        for i, letter in enumerate("KMGTPE", 1):
            v, suffix = n / base ** i, letter + ("" if si else "i")
            if v < 10:
                return f"{v:.1f}{suffix}"
            if v < base or letter == "E":
                return f"{int(v)}{suffix}"

    def row(label, cells):
        return label.ljust(8) + "".join(c.rjust(12) for c in cells) + "\n"

    t, used, free, shared, buffers, cache, avail = _FREE_MEM
    wide = a.has("w")
    out = row("", ["total", "used", "free", "shared"] + (["buffers", "cache"] if wide else ["buff/cache"]) + ["available"])
    out += row("Mem:", [size(x) for x in ((t, used, free, shared, buffers, cache, avail) if wide else (t, used, free, shared, buffers + cache, avail))])
    out += row("Swap:", [size(x) for x in _FREE_SWAP])
    if a.has("t"):
        out += row("Total:", [size(m + s) for m, s in zip(_FREE_MEM, _FREE_SWAP)])
    ctx.stdout.write(out)


def register(shell) -> None:
    r = shell.register_command
    r("whoami", lambda ctx: ctx.print(ctx.shell.user))
    r("hostname", lambda ctx: ctx.print(ctx.shell.hostname))
    r("uname", _uname)
    r("date", _date)
    r("clear", lambda ctx: 0)
    r("sudo", _sudo)
    for name in ("apt", "apt-get", "apt-cache", "dpkg"):
        r(name, _package_manager)
    r("su", _su)
    r("id", _id)
    r("uptime", _uptime)
    r("free", _free)
    r("nproc", lambda ctx: ctx.print("4"))
