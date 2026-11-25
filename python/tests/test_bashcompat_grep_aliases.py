# Ported from tests/Topwater.Tests/BashCompatGrepAliases.cs on main (recorded from real bash/GNU tools).
import pytest

from bashcompat import check

CASES_EgrepFgrep = [
    ('printf \'foo\\nbar\\nbaz\\n\' | egrep \'ba(r|z)\'; echo "rc=$?"', 'bar\nbaz\nrc=0\n', 0, True),
    ('printf \'foo\\nbar\\nbaz\\n\' | egrep -c \'o+\'; echo "rc=$?"', '1\nrc=0\n', 0, True),
    ('printf \'foo\\nbar\\nbaz\\n\' | egrep \'x|y\'; echo "rc=$?"', 'rc=1\n', 0, True),
    ('printf \'a.b\\naxb\\n\' | fgrep \'a.b\'; echo "rc=$?"', 'a.b\nrc=0\n', 0, True),
    ('printf \'a.b\\naxb\\n\' | fgrep -v \'a.b\'; echo "rc=$?"', 'axb\nrc=0\n', 0, True),
    ('printf \'a(b\\n\' | fgrep \'(\'; echo "rc=$?"', 'a(b\nrc=0\n', 0, True),
    ('printf \'a+b\\naab\\n\' | fgrep -n \'a+\'; echo "rc=$?"', '1:a+b\nrc=0\n', 0, True),
    ('printf \'x\\ny\\n\' > f; egrep -n \'x|y\' f; echo "rc=$?"', '1:x\n2:y\nrc=0\n', 0, True),
    ('printf \'x\\ny\\n\' > f; fgrep -l x f nofile; echo "rc=$?"', 'f\nrc=2\n', 0, False),
    ('printf \'one two\\n\' | egrep -o \'[a-z]{3}\'; echo "rc=$?"', 'one\ntwo\nrc=0\n', 0, True),
    ('printf \'one two\\n\' | egrep -w \'tw\'; echo "rc=$?"', 'rc=1\n', 0, True),
    ('printf \'ab\\nAB\\n\' | fgrep -i ab; echo "rc=$?"', 'ab\nAB\nrc=0\n', 0, True),
    ('printf \'ab\\ncd\\n\' | fgrep -e ab -e cd; echo "rc=$?"', 'ab\ncd\nrc=0\n', 0, True),
    ('printf \'ab\\ncd\\n\' | egrep \'(\'; echo "rc=$?"', 'rc=2\n', 0, False),
    ('printf \'a\\n\' | egrep -q a; echo "rc=$?"', 'rc=0\n', 0, True),
    ('printf \'a\\n\' | fgrep -x \'a\'; echo "rc=$?"', 'a\nrc=0\n', 0, True),
    ('printf \'a\\n\' | fgrep; echo "rc=$?"', 'rc=2\n', 0, False),
]


@pytest.mark.parametrize("script,stdout,exit_code,quiet", CASES_EgrepFgrep)
def test_egrep_fgrep(script, stdout, exit_code, quiet):
    check(script, stdout, exit_code, quiet)

