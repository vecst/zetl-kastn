#!/usr/bin/env bash
# Publishes the self-contained Linux bundle (Zetl, Kastn, installer, desktop
# files) to artifacts/publish/linux-x64. Run on Linux from a clean checkout:
# both executables must identify the commit being published.
#
#   scripts/publish-linux-x64.sh               release bundle
#   scripts/publish-linux-x64.sh --allow-dirty only while changing this script
set -euo pipefail

allow_dirty=0
case "${1:-}" in
    --allow-dirty) allow_dirty=1 ;;
    "") ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
esac

if [ "$(uname -s)" != Linux ]; then
    echo "Publish the Linux bundle on Linux, so the staged executables can be checked." >&2
    exit 1
fi

root=$(cd "$(dirname "$0")/.." && pwd)
publish_root=$root/artifacts/publish
final=$publish_root/linux-x64
staging=$publish_root/.linux-x64-staging-$$
backup=$publish_root/.linux-x64-previous-$$
cd "$root"

commit=$(git rev-parse HEAD)
if [ "$allow_dirty" = 0 ] && [ -n "$(git status --porcelain --untracked-files=all)" ]; then
    echo "The release worktree is not clean. Commit or remove these changes before publishing:" >&2
    git status --short --untracked-files=all >&2
    exit 1
fi

trap 'rm -rf "$staging"' EXIT
mkdir -p "$publish_root"
rm -rf "$staging" "$backup"
mkdir "$staging"

echo "Publishing Zetl and Kastn from commit $commit..."
for project in Zetl.App/Zetl.App.csproj Kastn.App/Kastn.App.csproj; do
    dotnet publish "$project" \
        -p:PublishProfile=linux-x64 \
        -p:PublishDir="$staging/" \
        -p:DebugType=None \
        -p:DebugSymbols=false \
        --nologo
done

cp LICENSE "$staging/LICENSE"
cp packaging/linux/install.sh packaging/linux/zetl.desktop packaging/linux/kastn.desktop \
    packaging/linux/zetl.svg packaging/linux/kastn.png packaging/linux/70-zetl-uinput.rules \
    "$staging/"
chmod 755 "$staging/install.sh"
for required in Zetl Kastn hotkeys.json LICENSE install.sh; do
    [ -f "$staging/$required" ] || { echo "The release bundle is missing $required." >&2; exit 1; }
done

zetl_version=$("$staging/Zetl" --version)
kastn_version=$("$staging/Kastn" --version)
case "$zetl_version" in
    *"+$commit") ;;
    *) echo "Zetl identifies '$zetl_version', not commit '$commit'." >&2; exit 1 ;;
esac
if [ "$zetl_version" != "$kastn_version" ]; then
    echo "Bundle version mismatch: Zetl '$zetl_version', Kastn '$kastn_version'." >&2
    exit 1
fi

# Replace the previous bundle only once everything above succeeded; the
# backup keeps it recoverable if the swap itself fails.
[ -d "$final" ] && mv "$final" "$backup"
if ! mv "$staging" "$final"; then
    [ -d "$backup" ] && [ ! -d "$final" ] && mv "$backup" "$final"
    exit 1
fi
rm -rf "$backup"
trap - EXIT

echo
echo "Linux release bundle ready: $final"
echo "Version: $zetl_version"
(cd "$final" && sha256sum Zetl Kastn)
echo "Install for this user with: $final/install.sh"
