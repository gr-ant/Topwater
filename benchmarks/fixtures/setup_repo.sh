set -e
mkdir -p ~/repo && cd ~/repo
git init -q -b main
git config user.email dev@ledger.example
mkdir -p . && cp ~/repo_src/c0/README.md README.md
mkdir -p src && cp ~/repo_src/c0/src/app.py src/app.py
mkdir -p src && cp ~/repo_src/c0/src/config.py src/config.py
git add -A && GIT_AUTHOR_NAME='Mara Lindqvist' GIT_COMMITTER_NAME='Mara Lindqvist' GIT_AUTHOR_DATE=2026-03-02T09:15:00Z GIT_COMMITTER_DATE=2026-03-02T09:15:00Z git -c user.name='Mara Lindqvist' commit -q -m 'Initial commit'
mkdir -p src && cp ~/repo_src/c1/src/billing.py src/billing.py
git add -A && GIT_AUTHOR_NAME='Mara Lindqvist' GIT_COMMITTER_NAME='Mara Lindqvist' GIT_AUTHOR_DATE=2026-03-05T14:02:00Z GIT_COMMITTER_DATE=2026-03-05T14:02:00Z git -c user.name='Mara Lindqvist' commit -q -m 'Add billing module'
mkdir -p src && cp ~/repo_src/c2/src/billing.py src/billing.py
git add -A && GIT_AUTHOR_NAME='Tomasz Wierzbicki' GIT_COMMITTER_NAME='Tomasz Wierzbicki' GIT_AUTHOR_DATE=2026-03-11T10:40:00Z GIT_COMMITTER_DATE=2026-03-11T10:40:00Z git -c user.name='Tomasz Wierzbicki' commit -q -m 'Billing: apply VAT'
mkdir -p docs && cp ~/repo_src/c3/docs/guide.md docs/guide.md
git add -A && GIT_AUTHOR_NAME='Priya Raman' GIT_COMMITTER_NAME='Priya Raman' GIT_AUTHOR_DATE=2026-04-01T08:30:00Z GIT_COMMITTER_DATE=2026-04-01T08:30:00Z git -c user.name='Priya Raman' commit -q -m 'Docs: user guide'
mkdir -p . && cp ~/repo_src/c4/VERSION VERSION
git add -A && GIT_AUTHOR_NAME='Mara Lindqvist' GIT_COMMITTER_NAME='Mara Lindqvist' GIT_AUTHOR_DATE=2026-04-20T16:45:00Z GIT_COMMITTER_DATE=2026-04-20T16:45:00Z git -c user.name='Mara Lindqvist' commit -q -m 'Release 1.0'
git tag v1.0
mkdir -p src && cp ~/repo_src/c5/src/config.py src/config.py
git add -A && GIT_AUTHOR_NAME='Tomasz Wierzbicki' GIT_COMMITTER_NAME='Tomasz Wierzbicki' GIT_AUTHOR_DATE=2026-05-14T11:05:00Z GIT_COMMITTER_DATE=2026-05-14T11:05:00Z git -c user.name='Tomasz Wierzbicki' commit -q -m 'Raise default timeout'
mkdir -p src && cp ~/repo_src/c6/src/billing.py src/billing.py
git add -A && GIT_AUTHOR_NAME='Priya Raman' GIT_COMMITTER_NAME='Priya Raman' GIT_AUTHOR_DATE=2026-06-02T13:20:00Z GIT_COMMITTER_DATE=2026-06-02T13:20:00Z git -c user.name='Priya Raman' commit -q -m 'Billing: discounts for gold customers'
mkdir -p . && cp ~/repo_src/c7/VERSION VERSION
git add -A && GIT_AUTHOR_NAME='Mara Lindqvist' GIT_COMMITTER_NAME='Mara Lindqvist' GIT_AUTHOR_DATE=2026-06-30T09:00:00Z GIT_COMMITTER_DATE=2026-06-30T09:00:00Z git -c user.name='Mara Lindqvist' commit -q -m 'Release 1.1'
git tag v1.1
mkdir -p src && cp ~/repo_src/c8/src/config.py src/config.py
mkdir -p src && cp ~/repo_src/c8/src/billing.py src/billing.py
git add -A && GIT_AUTHOR_NAME='Jonas Feld' GIT_COMMITTER_NAME='Jonas Feld' GIT_AUTHOR_DATE=2026-07-08T15:30:00Z GIT_COMMITTER_DATE=2026-07-08T15:30:00Z git -c user.name='Jonas Feld' commit -q -m 'Retry failed payments'
rm -rf ~/repo_src
