from pathlib import Path

HERE = Path(__file__).resolve().parents[1]


def test_third_party_notices_is_the_repository_copy():
    # the package carries jq's help text (_spec.py), so it ships the notice: a copy, since setuptools packs only this folder
    assert (HERE / "THIRD-PARTY-NOTICES.md").read_text() == (HERE.parent / "THIRD-PARTY-NOTICES.md").read_text()
    assert 'license-files = ["THIRD-PARTY-NOTICES.md"]' in (HERE / "pyproject.toml").read_text()
