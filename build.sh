#!/usr/bin/env bash
# build.sh - Build, package, and deploy DisplayTheSpire
# Usage: ./build.sh [--no-deploy]
#
# Required environment variables:
#   MEGADOT    Path to the MegaDot headless console executable (Godot editor build).
#              Example: export MEGADOT="/c/path/to/megadot-4.5.x/MegaDot_..._console.exe"
#
# Optional environment variables:
#   DOTNET     Path to dotnet.exe (default: /c/Program Files/dotnet/dotnet.exe)
#   MODS_DIR   Mod install directory  (default: typical Steam Windows path)
#   STS2DataDir  STS2 install dir, forwarded to dotnet build if set
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MOD_NAME="display_the_spire"

DOTNET="${DOTNET:-"/c/Program Files/dotnet/dotnet.exe"}"
MODS_DIR="${MODS_DIR:-"/c/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2/mods/$MOD_NAME"}"

if [[ -z "${MEGADOT:-}" ]]; then
    echo "ERROR: MEGADOT is not set."
    echo "  Set it to the path of the MegaDot headless console executable."
    echo "  Example: export MEGADOT=\"/c/path/to/megadot-4.5.x/MegaDot_..._console.exe\""
    exit 1
fi

STAGING="$SCRIPT_DIR/_pck_staging"
DLL_SRC="$SCRIPT_DIR/bin/Release/net9.0/$MOD_NAME.dll"
PCK_OUT="$SCRIPT_DIR/$MOD_NAME.pck"

DEPLOY=true
for arg in "$@"; do [[ "$arg" == "--no-deploy" ]] && DEPLOY=false; done

BUILD_EXTRA_ARGS=()
[[ -n "${STS2DataDir:-}" ]] && BUILD_EXTRA_ARGS+=("-p:STS2DataDir=$STS2DataDir")

# 1. Build 
echo "==> Building $MOD_NAME..."
"$DOTNET" build "$SCRIPT_DIR/$MOD_NAME.csproj" -c Release --nologo -v quiet "${BUILD_EXTRA_ARGS[@]}"
echo "    DLL: $DLL_SRC"

# 2. Stage PCK assets
echo "==> Staging PCK assets..."
rm -rf "$STAGING"
mkdir -p "$STAGING"

cp "$SCRIPT_DIR/mod_manifest.json" "$STAGING/"

cat > "$STAGING/project.godot" << 'EOF'
config_version=5
[application]
config/name="display_the_spire"
config/features=PackedStringArray("4.5", "Forward Plus")
EOF

cat > "$STAGING/export_presets.cfg" << 'EOF'
[preset.0]
name="Windows Desktop"
platform="Windows Desktop"
runnable=true
export_filter="all_resources"
EOF

# 3. Package PCK
echo "==> Packaging PCK..."
STAGING_WIN="$(cygpath -w "$STAGING")"
PCK_OUT_WIN="$(cygpath -w "$PCK_OUT")"

powershell.exe -Command "\
  \$out = & '$(cygpath -w "$MEGADOT")' \
    --headless \
    --export-pack 'Windows Desktop' '$PCK_OUT_WIN' \
    --path '$STAGING_WIN' 2>&1; \
  \$out | Select-String 'Storing|DONE|ERROR|error'" 2>&1 | grep -v "^$" || true

rm -rf "$STAGING"

[[ -f "$PCK_OUT" ]] || { echo "ERROR: PCK not created"; exit 1; }
echo "    PCK: $PCK_OUT"

# 4. Deploy 
if $DEPLOY; then
    echo "==> Deploying to $MODS_DIR..."
    mkdir -p "$MODS_DIR"
    cp "$PCK_OUT"                       "$MODS_DIR/"
    cp "$DLL_SRC"                       "$MODS_DIR/"
    cp "$SCRIPT_DIR/mod_manifest.json"  "$MODS_DIR/"
    echo "    Done."
else
    echo "==> Skipping deploy (--no-deploy)."
fi

echo "==> Build complete."
