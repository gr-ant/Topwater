# Ported from tests/Topwater.Tests/BashCompatArchives.cs on main (recorded from real bash/GNU tools).
import pytest

from bashcompat import check

CASES_MatchesRealTools = [
    ("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; tar cf t.tar d; tar tf t.tar", 'd/\nd/a.txt\nd/sub/\nd/sub/b.txt\n', 0, True),
    ("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; tar cvf t.tar d", 'd/\nd/a.txt\nd/sub/\nd/sub/b.txt\n', 0, True),
    ("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; tar czf t.tgz d && tar tzf t.tgz && tar tf t.tgz", 'd/\nd/a.txt\nd/sub/\nd/sub/b.txt\nd/\nd/a.txt\nd/sub/\nd/sub/b.txt\n', 0, True),
    ('mkdir -p d/sub; echo hello > d/a.txt; tar cf - d | tar tf -', 'd/\nd/a.txt\nd/sub/\n', 0, True),
    ('mkdir -p d/sub; echo hello > d/a.txt; tar czf - d | tar tzf -', 'd/\nd/a.txt\nd/sub/\n', 0, True),
    ('mkdir -p d; echo hello > d/a.txt; tar czf - d | tar tf -; echo "rc=$?"', 'rc=2\n', 0, False),
    ('tar xf nonexist.tar; echo "rc=$?"', 'rc=2\n', 0, False),
    ('tar cf x.tar nosuch; echo "rc=$?"; tar tf x.tar; echo "rc=$?"', 'rc=2\nrc=0\n', 0, False),
    ('tar; echo "rc=$?"', 'rc=2\n', 0, False),
    ('tar xf; echo "rc=$?"', 'rc=2\n', 0, False),
    ('mkdir d; echo hello > d/a.txt; tar cf t.tar d; tar tf t.tar d/a.txt nosuch; echo "rc=$?"', 'd/a.txt\nrc=2\n', 0, False),
    ("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; tar cf t.tar d; mkdir o && tar xf t.tar -C o && find o | sort && cat o/d/sub/b.txt", 'o\no/d\no/d/a.txt\no/d/sub\no/d/sub/b.txt\nx\ny\n', 0, True),
    ("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; tar cf t.tar d; mkdir o; tar xvf t.tar -C o", 'd/\nd/a.txt\nd/sub/\nd/sub/b.txt\n', 0, True),
    ('mkdir d; echo hello > d/a.txt; tar cf t.tar d; tar xOf t.tar d/a.txt', 'hello\n', 0, True),
    ('mkdir d; echo hello > d/a.txt; tar cf t.tar d; tar xOvf t.tar d/a.txt', 'hello\n', 0, False),
    ("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; tar cf t.tar d; mkdir o && tar xf t.tar -C o --strip-components=1 && find o | sort", 'o\no/a.txt\no/sub\no/sub/b.txt\n', 0, True),
    ("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; tar cf t.tar d; tar --strip-components=1 -tf t.tar", 'd/\nd/a.txt\nd/sub/\nd/sub/b.txt\n', 0, True),
    ('mkdir d; echo hello > d/a.txt; tar cf t.tar d; tar xzf t.tar; echo "rc=$?"', 'rc=2\n', 0, False),
    ('mkdir -p d/sub; echo hello > d/a.txt; tar czf t3.tgz -C d . ; tar tzf t3.tgz', './\n./a.txt\n./sub/\n', 0, True),
    ('echo hi > notar; tar tf notar; echo "rc=$?"', 'rc=2\n', 0, False),
    ("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; tar cf t.tar d; tar tf t.tar d/sub", 'd/sub/\nd/sub/b.txt\n', 0, True),
    ('mkdir d; echo hello > d/a.txt; echo top > t.txt; tar -c -f t.tar d/a.txt t.txt; tar -t -f t.tar', 'd/a.txt\nt.txt\n', 0, True),
    ('tar cf t.tar /tmp/nosuch-archive-test; echo "rc=$?"', 'rc=2\n', 0, False),
    ('mkdir d; echo hello > d/a.txt; tar cvf - d | wc -c', '10240\n', 0, False),
    ('mkdir d; echo hello > d/a.txt; tar czf x.tgz d; mkdir o; tar xzf - -C o < x.tgz; cat o/d/a.txt', 'hello\n', 0, True),
    ('mkdir d; echo hello > d/a.txt; tar cf - d | gzip > x.gz; zcat x.gz | tar xOf -', 'hello\n', 0, True),
    ('mkdir d; echo hello > d/a.txt; tar czf x.tgz d; cat x.tgz | tar xzf - -O d/a.txt', 'hello\n', 0, True),
    ('mkdir d; echo hello > d/a.txt; tar czf x.tgz d; tar xf - < x.tgz; echo "rc=$?"', 'rc=2\n', 0, False),
    ("mkdir d; echo hello > d/a.txt; tar czf - d | gzip -l | tail -1 | awk '{print $2, $4}'", '10240 stdout\n', 0, True),
    ('echo AP/+gGFiYw== | base64 -d > bin.dat; tar czf x.tgz bin.dat; mkdir o; tar xzf x.tgz -C o; md5sum bin.dat o/bin.dat | cut -c1-32', '676e0ec24cbc5d824eb54de42140de3d\n676e0ec24cbc5d824eb54de42140de3d\n', 0, True),
    ('echo AP/+gGFiYw== | base64 -d > bin.dat; gzip -c bin.dat | gunzip | md5sum; md5sum < bin.dat', '676e0ec24cbc5d824eb54de42140de3d  -\n676e0ec24cbc5d824eb54de42140de3d  -\n', 0, True),
    ('echo AP/+gGFiYw== | base64 -d > bin.dat; gzip -c bin.dat > b.gz; gunzip -c b.gz > b2; md5sum < b2; wc -c < b2', '676e0ec24cbc5d824eb54de42140de3d  -\n7\n', 0, True),
    ('echo AP/+gGFiYw== | base64 -d > bin.dat; cat bin.dat | gzip | gzip -d | od -An -tx1', ' 00 ff fe 80 61 62 63\n', 0, True),
    ('echo AP/+gGFiYw== | base64 -d > bin.dat; gzip bin.dat; ls; gunzip bin.dat.gz; ls; od -An -tx1 bin.dat', 'bin.dat.gz\nbin.dat\n 00 ff fe 80 61 62 63\n', 0, True),
    ('echo hello > f; gzip f; ls; gzip f.gz; echo "rc=$?"; gunzip f.gz; ls; cat f', 'f.gz\nrc=0\nf\nhello\n', 0, False),
    ('echo hello > f; gzip -k f; ls; gzip -k f; echo "rc=$?"', 'f\nf.gz\nrc=2\n', 0, False),
    ('echo hello > f; gzip -k f; gzip -f -k f; echo "rc=$?"; ls', 'rc=0\nf\nf.gz\n', 0, True),
    ('echo hello > f; gzip -c f | gunzip', 'hello\n', 0, True),
    ('echo hello > f; gzip f; cat f.gz | gunzip', 'hello\n', 0, True),
    ('echo hello > f; gzip f; zcat f.gz; zcat f.gz f.gz', 'hello\nhello\nhello\n', 0, True),
    ('echo hello > f; printf abc > e; gzip f; zcat f.gz e; echo "rc=$?"', 'hello\nrc=1\n', 0, False),
    ('printf abc > e; gunzip e; echo "rc=$?"', 'rc=2\n', 0, False),
    ('gunzip nosuch.gz; echo "rc=$?"', 'rc=1\n', 0, False),
    ('zcat nosuch; echo "rc=$?"', 'rc=1\n', 0, False),
    ('gzip nosuch; echo "rc=$?"', 'rc=1\n', 0, False),
    ('echo plain | gunzip; echo "rc=$?"', 'rc=1\n', 0, False),
    ('echo plain | gzip -d; echo "rc=$?"', 'rc=1\n', 0, False),
    ('echo hello > f; gzip -d f; echo "rc=$?"', 'rc=2\n', 0, False),
    ('mkdir dd; gzip dd; echo "rc=$?"', 'rc=2\n', 0, False),
    ('mkdir dd; gzip -q dd; echo "rc=$?"', 'rc=2\n', 0, True),
    ('echo hello > f; gzip -l f; echo "rc=$?"', 'rc=1\n', 0, False),
    ('gzip -l nosuch.gz; echo "rc=$?"', 'rc=1\n', 0, False),
    ('seq 1000 > big.txt; gzip -1 big.txt; gunzip big.txt.gz; md5sum < big.txt', '53d025127ae99ab79e8502aae2d9bea6  -\n', 0, True),
    ('seq 1000 > big.txt; gzip -9 -c big.txt | gzip -dc | tail -1', '1000\n', 0, True),
    ('echo hello > f; gzip -c f > x.gz; cat x.gz x.gz | gunzip', 'hello\nhello\n', 0, True),
    ('echo hello > f; gzip -c f > x.gz; gzip -c f >> x.gz; zcat x.gz', 'hello\nhello\n', 0, True),
    ('echo hello > f; gzip -t f; echo "rc=$?"', 'rc=1\n', 0, False),
    ('echo hello > f; gzip -c f | gzip -t; echo "rc=$?"', 'rc=0\n', 0, True),
    ('echo hello > f; gzip -c f > t.tgz; gunzip t.tgz; ls; cat t.tar', 'f\nt.tar\nhello\n', 0, True),
    ('echo hello > f; gzip -dcf f', 'hello\n', 0, True),
    ('echo hello > f; gzip -f f; ls; gzip -d f.gz -k; ls', 'f.gz\nf\nf.gz\n', 0, True),
    ('seq 1000 > big.txt; gzip -c big.txt | head -c 100 | gunzip > /dev/null; echo "rc=$?"', 'rc=1\n', 0, False),
    ('echo hello > f; gzip -v f 2>&1 | cut -d: -f1', 'f\n', 0, True),
    ('gzip -dc < nosuch; echo "rc=$?"', 'rc=1\n', 0, False),
    ('echo hi | gzip | gunzip', 'hi\n', 0, True),
    ("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; echo top > t.txt; zip a.zip t.txt", '  adding: t.txt (stored 0%)\n', 0, True),
    ("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; zip -r a.zip d", '  adding: d/ (stored 0%)\n  adding: d/a.txt (stored 0%)\n  adding: d/sub/ (stored 0%)\n  adding: d/sub/b.txt (stored 0%)\n', 0, True),
    ("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; echo top > t.txt; zip a.zip t.txt; zip -r a.zip d; zip a.zip t.txt", '  adding: t.txt (stored 0%)\n  adding: d/ (stored 0%)\n  adding: d/a.txt (stored 0%)\n  adding: d/sub/ (stored 0%)\n  adding: d/sub/b.txt (stored 0%)\nupdating: t.txt (stored 0%)\n', 0, True),
    ('echo top > t.txt; zip -q b.zip t.txt nosuch; echo "rc=$?"', 'rc=0\n', 0, True),
    ('echo top > t.txt; zip b.zip t.txt nosuch; echo "rc=$?"', '\tzip warning: name not matched: nosuch\n  adding: t.txt (stored 0%)\nrc=0\n', 0, True),
    ('mkdir d; zip c.zip d', '  adding: d/ (stored 0%)\n', 0, True),
    ("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; echo top > t.txt; zip -qr a.zip t.txt d; unzip -l a.zip | cut -c1-9,30-", 'Archive: \n  Length  Name\n--------- ----\n        4 t.txt\n        0 d/\n        6 d/a.txt\n        0 d/sub/\n        4 d/sub/b.txt\n--------- -------\n       14 5 files\n', 0, True),
    ("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; echo top > t.txt; zip -qr a.zip t.txt d; mkdir o && unzip a.zip -d o; cat o/d/a.txt", 'Archive:  a.zip\n extracting: o/t.txt                 \n   creating: o/d/\n extracting: o/d/a.txt               \n   creating: o/d/sub/\n extracting: o/d/sub/b.txt           \nhello\n', 0, True),
    ("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; echo top > t.txt; zip -qr a.zip t.txt d; mkdir o && unzip -q a.zip -d o; unzip -o a.zip -d o", 'Archive:  a.zip\n extracting: o/t.txt                 \n extracting: o/d/a.txt               \n extracting: o/d/sub/b.txt           \n', 0, True),
    ("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; echo top > t.txt; zip -qr a.zip t.txt d; unzip -q -o a.zip -d o; find o | sort", 'o\no/d\no/d/a.txt\no/d/sub\no/d/sub/b.txt\no/t.txt\n', 0, True),
    ('mkdir -p d; echo hello > d/a.txt; echo top > t.txt; zip -qr a.zip t.txt d; unzip -p a.zip d/a.txt t.txt', 'top\nhello\n', 0, True),
    ('unzip nosuch.zip; echo "rc=$?"', 'rc=9\n', 0, False),
    ('echo top > t.txt; zip -q a.zip t.txt; unzip -l a.zip nosuch; echo "rc=$?"', 'Archive:  a.zip\n  Length      Date    Time    Name\n---------  ---------- -----   ----\n---------                     -------\n        0                     0 files\nrc=11\n', 0, True),
    ('zip -r e.zip nosuch; echo "rc=$?"', '\tzip warning: name not matched: nosuch\n\nzip error: Nothing to do! (try: zip -r e.zip . -i nosuch)\nrc=12\n', 0, True),
    ('zip y.zip nosuch; echo "rc=$?"', '\tzip warning: name not matched: nosuch\n\nzip error: Nothing to do! (y.zip)\nrc=12\n', 0, True),
    ('mkdir -p d; echo hello > d/a.txt; echo top > t.txt; zip -qr a.zip t.txt d; mkdir o; cd o && unzip -n ../a.zip; echo "rc=$?"', 'Archive:  ../a.zip\n extracting: t.txt                   \n   creating: d/\n extracting: d/a.txt                 \nrc=0\n', 0, True),
    ('mkdir -p d; echo hello > d/a.txt; echo top > t.txt; zip -qr a.zip t.txt d; mkdir o; cd o && unzip -q ../a.zip; unzip ../a.zip t.txt nosuch < /dev/null; echo "rc=$?"', 'Archive:  ../a.zip\nrc=11\n', 0, False),
    ('echo notzip > bad.zip; unzip -l bad.zip; echo "rc=$?"', 'Archive:  bad.zip\nrc=9\n', 0, False),
    ('seq 1000 > big.txt; echo AP/+gGFiYw== | base64 -d > bin.dat; zip big.zip big.txt bin.dat; rm big.txt bin.dat; unzip big.zip; md5sum big.txt; od -An -tx1 bin.dat', '  adding: big.txt (deflated 53%)\n  adding: bin.dat (stored 0%)\nArchive:  big.zip\n  inflating: big.txt                 \n extracting: bin.dat                 \n53d025127ae99ab79e8502aae2d9bea6  big.txt\n 00 ff fe 80 61 62 63\n', 0, True),
    ("mkdir -p d/sub; echo hello > d/a.txt; printf 'x\\ny\\n' > d/sub/b.txt; cd d && zip -r ../g.zip . ; unzip -l ../g.zip | cut -c1-9,30-", '  adding: a.txt (stored 0%)\n  adding: sub/ (stored 0%)\n  adding: sub/b.txt (stored 0%)\nArchive: \n  Length  Name\n--------- ----\n        6 a.txt\n        0 sub/\n        4 sub/b.txt\n--------- -------\n       10 3 files\n', 0, True),
    ('mkdir -p d; echo hello > d/a.txt; echo top > t.txt; zip -qr a.zip d t.txt; unzip -d o3 a.zip t.txt; ls o3', 'Archive:  a.zip\n extracting: o3/t.txt                \nt.txt\n', 0, True),
    ('mkdir -p d; echo hello > d/a.txt; echo top > t.txt; zip -qr - d t.txt > s.zip; unzip -l s.zip | cut -c1-9,30-', 'Archive: \n  Length  Name\n--------- ----\n        0 d/\n        6 d/a.txt\n        4 t.txt\n--------- -------\n       10 3 files\n', 0, True),
    ('echo top > t.txt; zip a t.txt; ls', '  adding: t.txt (stored 0%)\na.zip\nt.txt\n', 0, True),
    ('mkdir -p d/sub; echo hello > d/a.txt; printf \'x\\ny\\n\' > d/sub/b.txt; zip -qr a.zip d; unzip -l a.zip "d/*.txt" | cut -c1-9,30-', 'Archive: \n  Length  Name\n--------- ----\n        6 d/a.txt\n        4 d/sub/b.txt\n--------- -------\n       10 2 files\n', 0, True),
]


@pytest.mark.parametrize("script,stdout,exit_code,quiet", CASES_MatchesRealTools)
def test_matches_real_tools(script, stdout, exit_code, quiet):
    check(script, stdout, exit_code, quiet)


# NOTE: 7 [Fact] method(s) in the C# file need porting by hand.



# ---- hand-ported [Fact] tests ----
import gzip as _gzip
import io
import tarfile
import zipfile

from topwater import Shell

REAL_TGZ = "H4sIAAAAAAAAA+3WSwrCMBAG4Kx7ipygSSSP8yRGUCgKtoW68+RqWsFFFj4Wk4j+3yZZFDLwM9OJgpGTiXNmOZP8XO5KG62dVsbO39mVkowb+tIYG/vBHzkv8dQ3iiLs9m30A+Ebc8DW6if5uyx/l+6MS8KaHv48f3a9nH1Y164CKonCt8NE2f3v9L/N+1+lA/1fwHbTdYemdhVQSxT9GIh3wM/3Pyelwv5Xwj3/QPoPeD3/dZ6/M5j/RUzNCdMfAAAAAAAAAAAAAADgh9wAKjqjJQAoAAA="
REAL_GZ = "H4sICHXDk2UAA2Jpbi5kYXQAY/j/ryExKRkAZbSQqQcAAAA="
BIN = bytes([0, 0xFF, 0xFE, 0x80, 0x61, 0x62, 0x63])


def fresh(**limits):
    sh = Shell()
    for k, v in limits.items():
        setattr(sh, k, v)
    sh.run("mkdir -p /tmp/w && cd /tmp/w")
    return sh


def _tar(entries, gz=False):
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w", format=tarfile.GNU_FORMAT) as t:
        for name, kind, data, link in entries:
            ti = tarfile.TarInfo(name)
            ti.type, ti.linkname, ti.size = kind, link, len(data)
            t.addfile(ti, io.BytesIO(data))
    return _gzip.compress(buf.getvalue()) if gz else buf.getvalue()


def test_real_gzip_extracts():
    sh = fresh()
    import base64
    sh.fs.write_bytes("/tmp/w/r.tgz", base64.b64decode(REAL_TGZ))
    sh.fs.write_bytes("/tmp/w/r.gz", base64.b64decode(REAL_GZ))
    r = sh.run("mkdir t; tar xzf r.tgz -C t; zcat r.gz > g; cat t/d/a.txt")
    assert r.exit_code == 0 and r.stdout == "hello\n"
    assert sh.fs.read_bytes("/tmp/w/t/d/bin.dat") == BIN and sh.fs.read_bytes("/tmp/w/g") == BIN


def test_archives_made_here_are_standard():
    sh = fresh()
    sh.run("mkdir -p d/sub")
    sh.fs.write_bytes("/tmp/w/d/bin.dat", BIN)
    sh.fs.write_text("/tmp/w/d/sub/" + "n" * 120, "long name\n")
    assert sh.run("tar czf a.tgz d; gzip -k d/bin.dat; zip -qr a.zip d").exit_code == 0
    with tarfile.open(fileobj=io.BytesIO(sh.fs.read_bytes("/tmp/w/a.tgz")), mode="r:gz") as t:
        assert t.getnames() == ["d", "d/bin.dat", "d/sub", "d/sub/" + "n" * 120]
        assert t.extractfile("d/bin.dat").read() == BIN
    assert _gzip.decompress(sh.fs.read_bytes("/tmp/w/d/bin.dat.gz")) == BIN
    zi = zipfile.ZipFile(io.BytesIO(sh.fs.read_bytes("/tmp/w/a.zip"))).getinfo("d/bin.dat")
    assert zi.external_attr >> 16 == 0x81A4


def test_binary_survives_pipes():
    import random
    sh = fresh()
    data = random.Random(1).randbytes(5000)
    sh.fs.write_bytes("/tmp/w/bin", data)
    r = sh.run("gzip -c bin > b.gz; cat b.gz | gunzip > o1; tar czf - bin | gzip > d.gz; zcat d.gz | gunzip | tar xOf - > o3; cat bin >> o7; cat bin >> o7")
    assert r.stderr == ""
    assert sh.fs.read_bytes("/tmp/w/o1") == data and sh.fs.read_bytes("/tmp/w/o3") == data
    assert sh.fs.read_bytes("/tmp/w/o7") == data + data


def test_tar_never_writes_outside_target():
    sh = fresh()
    sh.fs.write_bytes("/tmp/w/evil.tar", _tar([("../up.txt", tarfile.REGTYPE, b"x", ""), ("/abs.txt", tarfile.REGTYPE, b"x", ""),
                                                ("good.txt", tarfile.REGTYPE, b"good\n", ""), ("lnk", tarfile.SYMTYPE, b"", "/etc"),
                                                ("hl", tarfile.LNKTYPE, b"", "../outside"), ("hl3", tarfile.LNKTYPE, b"", "good.txt")]))
    r = sh.run("mkdir x && tar xf evil.tar -C x; echo rc=$?; cd x && find . | sort; cat hl3")
    assert r.stdout == "rc=2\n.\n./abs.txt\n./good.txt\n./hl3\ngood\n"
    assert "tar: ../up.txt: Member name contains '..'" in r.stderr
    assert not any(sh.fs.exists(p) for p in ("/tmp/up.txt", "/tmp/w/up.txt", "/abs.txt"))


def test_extraction_limits():
    sh = fresh(max_extract_bytes=1 << 20, max_extract_entries=50)
    zeros = bytes(2 << 20)
    sh.fs.write_bytes("/tmp/w/bomb.gz", _gzip.compress(zeros))
    sh.fs.write_bytes("/tmp/w/bomb.tar", _tar([("z", tarfile.REGTYPE, zeros, "")]))
    sh.fs.write_bytes("/tmp/w/many.tar", _tar([(f"f{i}", tarfile.REGTYPE, b"x", "") for i in range(60)]))
    limit = "output exceeds the 1048576-byte extraction limit; stopping"
    for cmd, code, msg in [("gunzip bomb.gz", 1, f"gunzip: {limit}"), ("tar xf bomb.tar -C x", 2, f"tar: {limit}"),
                           ("tar tf many.tar > /dev/null", 2, "tar: archive exceeds the 50-entry extraction limit; stopping")]:
        r = sh.run(f"rm -rf x; mkdir x; {cmd}")
        assert (r.exit_code, r.stderr) == (code, msg + "\n")
    assert sh.fs.is_file("/tmp/w/bomb.gz") and not sh.fs.exists("/tmp/w/x/z")


def test_corrupt_gzip_fails():
    sh = fresh()
    data = _gzip.compress("\n".join(map(str, range(1, 1001))).encode())
    sh.fs.write_bytes("/tmp/w/cut.gz", data[: len(data) // 2])
    bad = bytearray(data)
    bad[-6] ^= 0xFF
    sh.fs.write_bytes("/tmp/w/bad.gz", bytes(bad))
    r = sh.run("gunzip cut.gz; echo rc=$?; zcat bad.gz | wc -c; ls")
    assert r.stdout == "rc=1\n0\nbad.gz\ncut.gz\n"
    assert r.stderr == "\ngzip: cut.gz: unexpected end of file\n\ngzip: bad.gz: invalid compressed data--format violated\n"
