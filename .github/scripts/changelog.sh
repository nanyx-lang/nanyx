#!/usr/bin/env bash
# Writes the changelog of one part of the repository from the pull requests that changed it, release by release.
#
#   changelog.sh <path> <tag prefix> <version> <out dir> [footer]
#
# Each release is a tag `<tag prefix><version>`. A release's section lists the commits between the release before it
# and its own tag that changed a file under <path>, one line each: its title and a link to its pull request (a squash
# merge's subject ends `(#n)`), or to the commit when it has none. <version>, the release being made, takes the commits
# since the last tag up to HEAD. [footer], if given, ends every section.
#
# It writes <out dir>/release-notes.md, the section of <version> without its heading, for the GitHub release, and
# <out dir>/CHANGELOG.md, every section, newest first. Needs the tags: check out with `fetch-depth: 0`.
set -euo pipefail

path=$1
prefix=$2
version=$3
out=$4
footer=${5:-}
repository=${GITHUB_REPOSITORY:-nanyx-lang/nanyx}

mkdir -p "$out"

# The versions released before <version>, oldest first.
mapfile -t earlier < <(git tag -l "${prefix}*" | sed "s|^${prefix}||" | grep -vxF "$version" | sort -V)

# The lines of the section for the commits in `range`.
section() {
  local range=$1 lines
  lines=$(git log --no-merges --format='%H %s' "$range" -- "$path" | while read -r sha subject; do
    if [[ $subject =~ ^(.*)\ \(#([0-9]+)\)$ ]]; then
      echo "- ${BASH_REMATCH[1]} ([#${BASH_REMATCH[2]}](https://github.com/$repository/pull/${BASH_REMATCH[2]}))"
    else
      echo "- $subject ([${sha:0:7}](https://github.com/$repository/commit/$sha))"
    fi
  done)
  if [[ -z $lines ]]; then echo "Version bump only."; else echo "$lines"; fi
  if [[ -n $footer ]]; then printf '\n%s\n' "$footer"; fi
}

if ((${#earlier[@]})); then
  current=$(section "${prefix}${earlier[-1]}..HEAD")
else
  current=$(section HEAD)
fi
printf '%s\n' "$current" > "$out/release-notes.md"

{
  printf '# Changelog\n\n## %s\n\n%s\n' "$version" "$current"
  for ((i = ${#earlier[@]} - 1; i >= 0; i--)); do
    tag="${prefix}${earlier[i]}"
    if ((i > 0)); then range="${prefix}${earlier[i - 1]}..$tag"; else range=$tag; fi
    printf '\n## %s\n\n%s\n' "${earlier[i]}" "$(section "$range")"
  done
} > "$out/CHANGELOG.md"
