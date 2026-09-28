#!/bin/sh
#
# Validates commit messages against Conventional Commits v1.0.0.
#
# .githooks/commit-msg enforces the same rules locally, but a commit made through
# the GitHub web editor never runs a local hook - and there is already one such
# commit in this history. This is the check that cannot be bypassed, so CI runs it
# on every push and pull request.
#
# usage: sh ci/check-commits.sh [<git-range>]
#        sh ci/check-commits.sh origin/main..HEAD
#        sh ci/check-commits.sh            # validates HEAD only
#
# See CONTRIBUTING.md for the type and scope vocabulary.

set -eu

range="${1:-}"
types='feat|fix|build|chore|ci|docs|style|refactor|perf|test|revert'

# Three cases, and the distinction is load bearing.
#
# A two-ended range needs its base validated: a first push reports an all-zero
# "before" sha and a force push can report a commit that no longer exists, and
# neither is worth failing the build over, so those fall back to the tip.
#
# Anything else - an empty argument, or a single revision such as "HEAD" or
# "<sha>^!" - goes straight to git log. Treating a single revision as a range with
# a missing base would send it down the fallback path and validate HEAD instead of
# the commit actually asked about, which silently passes the wrong thing. That bug
# was real: it made every per-commit check in .githooks/pre-push validate HEAD.
#
# The null sha needs an explicit test rather than a rev-parse, because
# 'git rev-parse --verify' resolves 0000... to itself while 'git log' then rejects
# the range and aborts.
case "$range" in
    "")
        echo "no range given; checking HEAD only"
        commits=$(git log --format=%H -1)
        ;;

    *..*)
        base="${range%%..*}"
        if [ "$base" != "0000000000000000000000000000000000000000" ] &&
           git cat-file -e "${base}^{commit}" 2>/dev/null; then
            commits=$(git log --format=%H "$range")
            echo "checking $(printf '%s\n' "$commits" | wc -l | tr -d ' ') commit(s) in $range"
        else
            echo "no usable base in '$range'; checking HEAD only"
            commits=$(git log --format=%H -1)
        fi
        ;;

    *)
        commits=$(git log --format=%H "$range")
        echo "checking $(printf '%s\n' "$commits" | wc -l | tr -d ' ') commit(s) in $range"
        ;;
esac

fail=0

for sha in $commits; do
    subject=$(git log -1 --format=%s "$sha")

    # git generates these itself.
    case "$subject" in
        Merge*|Revert*|fixup!*|squash!*) continue ;;
    esac

    if ! printf '%s' "$subject" | grep -Eq "^($types)(\([a-z0-9._/-]+\))?!?: .+"; then
        echo "not conventional : $sha $subject" >&2
        fail=1
        continue
    fi

    len=$(printf '%s' "$subject" | wc -c | tr -d '[:space:]')
    if [ "$len" -gt 100 ]; then
        echo "header too long  : $sha ($len chars) $subject" >&2
        fail=1
    fi

    # BREAKING CHANGE must be uppercase, per the specification.
    body=$(git log -1 --format=%B "$sha")
    if printf '%s\n' "$body" | grep -qi 'breaking[ -]change' &&
       ! printf '%s\n' "$body" | grep -q 'BREAKING CHANGE'; then
        echo "breaking change must be uppercase : $sha" >&2
        fail=1
    fi
done

if [ "$fail" -ne 0 ]; then
    {
        echo
        echo "One or more commit messages do not follow Conventional Commits v1.0.0."
        echo "See CONTRIBUTING.md for the type and scope vocabulary."
        echo
        echo "A commit made through the GitHub web editor bypasses the local hook;"
        echo "amend the message with 'git commit --amend' before pushing, or edit it"
        echo "on the commit's page on GitHub."
    } >&2
    exit 1
fi

echo "all commit messages are conventional"
