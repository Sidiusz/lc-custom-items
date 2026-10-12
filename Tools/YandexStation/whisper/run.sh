#!/bin/sh
# Сравнение Vosk и Whisper на фразах для Алисы (Linux, процессор).
#   ./run.sh <модель whisper .bin> <папка со звуком> [префиксы файлов, через запятую]
# Звук делает make_audio.py (Silero TTS и espeak-ng). Модель Vosk берётся из CustomItems/Vosk/model-ru
# (final.mdl распаковывается во временную папку). Переменные THREADS, SHORT, NOPROMPT — см. test/Program.cs.
set -e
cd "$(dirname "$0")"
ROOT=$(cd ../../.. && pwd)
TMP=${TMPDIR:-/tmp}/alice-whisper-test
if [ ! -f "$TMP/model-ru/am/final.mdl" ]; then
    rm -rf "$TMP/model-ru"
    mkdir -p "$TMP"
    cp -r "$ROOT/CustomItems/Vosk/model-ru" "$TMP/model-ru"
    unzip -oq "$TMP/model-ru/am/final.mdl.zip" -d "$TMP/model-ru/am"
    rm "$TMP/model-ru/am/final.mdl.zip"
fi
dotnet build test -c Release -v q -o "$TMP/out" >/dev/null
# Библиотеки из пакета ищут друг друга по soname (libggml-whisper.so.0)
for f in "$TMP"/out/lib*whisper*.so; do ln -sf "$(basename "$f")" "$f.0"; done
LD_LIBRARY_PATH="$TMP/out" dotnet "$TMP/out/whispertest.dll" "$TMP/model-ru" "$1" "$ROOT/CustomItems/intents.txt" phrases.txt "$2" "${3:-s,e}"
