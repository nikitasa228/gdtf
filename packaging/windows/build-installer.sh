#!/usr/bin/env bash
# Builds the Windows installer of Candela on Linux: Candela-Setup-<version>-win-x64.exe in artifacts/windows/.
#
#   packaging/windows/build-installer.sh [--out DIR] [--no-ready-to-run] [--redist vc_redist.x64.exe] [--no-redist]
#
# Steps: publish the editor (candela-app) and the command-line tool (candela) for win-x64, self-contained, into ONE
# folder (no trimming: reflection and JSON serialisation; ReadyToRun for a faster start); complete it into the installed
# layout (data/specs, samples, docs, licences — packaging/windows/winpack.py stage); check that every native DLL is x64
# and that everything it imports is shipped, part of Windows, or an expected external component (winpack.py check);
# fetch Microsoft's Visual C++ 2015-2022 x64 redistributable into a new temporary folder and verify its Authenticode
# signature against Microsoft's root certificate (pinned fingerprint) with osslsigncode; run makensis.
#
# Needs: .NET 8 SDK, python3, makensis (apt-get install nsis), osslsigncode and curl (for the redistributable).
# The version comes from Directory.Build.props (CandelaVersion / CandelaPreview).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
OUT="$ROOT/artifacts/windows"
READY_TO_RUN=true
REDIST=""
WITH_REDIST=true

# Microsoft's VC++ redistributable (always the latest 2015-2022 x64 build) and the root its signature chains to:
# "Microsoft Root Certificate Authority 2011", SHA-256 fingerprint below (published by Microsoft PKI).
REDIST_URL="https://aka.ms/vs/17/release/vc_redist.x64.exe"
MS_ROOT_URL="http://www.microsoft.com/pki/certs/MicRooCerAut2011_2011_03_22.crt"
MS_ROOT_SHA256="84:7D:F6:A7:84:97:94:3F:27:FC:72:EB:93:F9:A6:37:32:0A:02:B5:61:D0:A9:1B:09:E8:7A:78:07:ED:7C:61"

while [ $# -gt 0 ]; do
  case "$1" in
    --out) OUT="$(mkdir -p "$2" && cd "$2" && pwd)"; shift 2 ;;
    --no-ready-to-run) READY_TO_RUN=false; shift ;;
    --redist) REDIST="$(cd "$(dirname "$2")" && pwd)/$(basename "$2")"; shift 2 ;;
    --no-redist) WITH_REDIST=false; shift ;;
    -h|--help) sed -n '2,15p' "$0"; exit 0 ;;
    *) echo "Unknown option '$1' (see --help)." >&2; exit 2 ;;
  esac
done

need() { command -v "$1" >/dev/null 2>&1 || { echo "error: '$1' is required ($2)." >&2; exit 1; }; }
need dotnet ".NET 8 SDK"
need python3 "Python 3"
need makensis "apt-get install nsis"
if $WITH_REDIST; then
  need osslsigncode "apt-get install osslsigncode"
  need openssl "apt-get install openssl"
  [ -n "$REDIST" ] || need curl "apt-get install curl"
fi

APP="$ROOT/src/Candela.App/Candela.App.csproj"
CLI="$ROOT/src/Candela.Cli/Candela.Cli.csproj"
VERSION="$(dotnet msbuild "$APP" -nologo -getProperty:Version | tail -n 1 | tr -d '\r')"
FILE_VERSION="$(dotnet msbuild "$APP" -nologo -getProperty:FileVersion | tail -n 1 | tr -d '\r')"
SETUP="$OUT/Candela-Setup-$VERSION-win-x64.exe"
echo "== Candela $VERSION ($FILE_VERSION) for win-x64 -> $SETUP"

PUBLISH="$OUT/publish"
GENERATED="$OUT/generated"
rm -rf "$PUBLISH" "$GENERATED"
mkdir -p "$PUBLISH" "$GENERATED"

echo "== Publishing the editor and the command-line tool (self-contained, ReadyToRun: $READY_TO_RUN)"
for project in "$APP" "$CLI"; do
  dotnet publish "$project" -nologo -c Release -r win-x64 --self-contained true \
    -p:PublishReadyToRun="$READY_TO_RUN" -p:PublishTrimmed=false -p:DebugType=portable -o "$PUBLISH"
done

echo "== Completing the installed layout"
python3 -I "$HERE/winpack.py" stage "$ROOT" "$PUBLISH" "$GENERATED"
echo "== Checking the native dependencies"
python3 -I "$HERE/winpack.py" check "$PUBLISH"
python3 -I "$HERE/winpack.py" resources "$PUBLISH/candela-app.exe" "$PUBLISH/candela.exe"

NSIS_REDIST=()
if $WITH_REDIST; then
  if [ -z "$REDIST" ]; then
    DOWNLOAD="$(mktemp -d /tmp/candela-vcredist.XXXXXX)"   # a new, empty folder for the download
    echo "== Downloading $REDIST_URL into $DOWNLOAD"
    curl -sSfL --retry 3 -o "$DOWNLOAD/vc_redist.x64.exe" "$REDIST_URL"
    REDIST="$DOWNLOAD/vc_redist.x64.exe"
  fi
  echo "== Verifying $REDIST"
  SIZE=$(stat -c %s "$REDIST")
  if [ "$SIZE" -lt 10000000 ] || [ "$SIZE" -gt 80000000 ]; then
    echo "error: $REDIST has an unexpected size ($SIZE bytes)." >&2; exit 1
  fi
  PKI="$(mktemp -d /tmp/candela-mspki.XXXXXX)"
  curl -sSfL --retry 3 -o "$PKI/root.crt" "$MS_ROOT_URL"
  FINGERPRINT="$(openssl x509 -inform DER -in "$PKI/root.crt" -noout -fingerprint -sha256 | cut -d= -f2)"
  if [ "$FINGERPRINT" != "$MS_ROOT_SHA256" ]; then
    echo "error: the downloaded Microsoft root certificate has fingerprint $FINGERPRINT, expected $MS_ROOT_SHA256." >&2; exit 1
  fi
  openssl x509 -inform DER -in "$PKI/root.crt" -out "$PKI/root.pem"
  REPORT="$(osslsigncode verify -in "$REDIST" 2>&1 || true)"
  echo "$REPORT" | grep -q "Subject: /C=US/ST=Washington/L=Redmond/O=Microsoft Corporation/CN=Microsoft Corporation" \
    || { echo "error: $REDIST is not signed by Microsoft Corporation." >&2; exit 1; }
  # A timestamped signature stays valid after the signing certificate expired: verify it at the signing time.
  STAMP="$(echo "$REPORT" | sed -n 's/^[[:space:]]*Timestamp time: //p' | head -1)"
  [ -n "$STAMP" ] || { echo "error: $REDIST has no signing timestamp." >&2; exit 1; }
  osslsigncode verify -CAfile "$PKI/root.pem" -TSA-CAfile "$PKI/root.pem" -time "$(date -u -d "$STAMP" +%s)" -in "$REDIST" \
    | grep -q "^Signature verification: ok" || { echo "error: the Authenticode signature of $REDIST does not verify." >&2; exit 1; }
  REDIST_VERSION="$(python3 -I "$HERE/winpack.py" version "$REDIST")"
  REDIST_BUILD="$(echo "$REDIST_VERSION" | cut -d. -f3)"
  echo "   signed by Microsoft Corporation (timestamp $STAMP), version $REDIST_VERSION, $SIZE bytes"
  NSIS_REDIST=(-DREDIST="$REDIST" -DREDIST_BUILD="$REDIST_BUILD")
fi

echo "== Building the installer"
# makensis needs a UTF-8 locale for non-ASCII file names (samples/Фокина.mvr); with the C locale it crashes.
LC_ALL=C.UTF-8 makensis -NOCD -INPUTCHARSET UTF8 -V2 \
  -DVERSION="$VERSION" -DFILE_VERSION="$FILE_VERSION" -DSTAGE="$PUBLISH" -DGENERATED="$GENERATED" \
  "${NSIS_REDIST[@]}" -DOUTFILE="$SETUP" "$HERE/installer.nsi"

( cd "$OUT" && sha256sum "$(basename "$SETUP")" > "$(basename "$SETUP").sha256" )
echo "== Done"
ls -l "$SETUP"
cat "$SETUP.sha256"
