#!/usr/bin/env bash
# Compiles the Stealth assemblies the way Unity does, so script errors surface here
# rather than after a menu click.
#
# Why this exists: when compilation fails, Unity keeps running the last assemblies that
# built successfully. The editor tool then silently runs an OLD version of itself, which
# looks identical to "the change didn't work" — the same failure was mistaken for a logic
# bug several times before this check existed.
set -u
U="/c/Program Files/Unity/Hub/Editor/6000.5.0b11/Editor/Data"
CSC="$U/DotNetSdk/sdk/8.0.318/Roslyn/bincore/csc.dll"
DOTNET="/c/Program Files/dotnet/dotnet"
OUT="${TMP:-/c/Users/$USER/AppData/Local/Temp}/stealth-compile"
mkdir -p "$OUT"
RSP="$OUT/refs.rsp"

# UnityEditor.CoreModule.dll lives under Managed/UnityEngine. Do NOT also reference the
# legacy monolithic Managed/UnityEditor.dll: it duplicates those types and every attribute
# such as [MenuItem] becomes ambiguous (CS0433).
: > "$RSP"
for d in "$U/Managed/UnityEngine"/*.dll "$U/NetStandard/ref/2.1.0/netstandard.dll"; do
  echo "-r:\"$(cygpath -w "$d")\"" >> "$RSP"
done
for d in Library/ScriptAssemblies/*.dll; do
  case "$(basename "$d")" in
    Blocks.Gameplay.Stealth.dll|Blocks.Gameplay.Stealth.Editor.dll) continue ;;
  esac
  echo "-r:\"$(cygpath -w "$(pwd)/$d")\"" >> "$RSP"
done

run() { "$DOTNET" "$CSC" -nologo -target:library -nostdlib+ -langversion:9.0 "@$(cygpath -w "$RSP")" "$@"; }

echo "== Blocks.Gameplay.Stealth =="
run -out:"$(cygpath -w "$OUT/Stealth.dll")" Assets/Stealth/Scripts/Runtime/*.cs 2>&1 | grep "error CS"
rt=${PIPESTATUS[0]}

echo "== Blocks.Gameplay.Stealth.Editor =="
run -r:"$(cygpath -w "$OUT/Stealth.dll")" -out:"$(cygpath -w "$OUT/StealthEditor.dll")" Assets/Stealth/Scripts/Editor/*.cs 2>&1 | grep "error CS"
ed=${PIPESTATUS[0]}

if [ "$rt" -eq 0 ] && [ "$ed" -eq 0 ]; then echo "COMPILES CLEAN"; exit 0; fi
echo "COMPILE FAILED"; exit 1
