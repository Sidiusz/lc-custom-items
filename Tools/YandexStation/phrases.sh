#!/usr/bin/env bash
# Временные фразы колонки -> Assets/Sources/Art/Audio/YandexStation/Phrases/Placeholder*.wav
# Синтезатор espeak-ng (женский русский голос) и короткий звук «Алиса слушает» перед фразой.
# Это заглушки: свои записи кладите в ту же папку (любые .wav), а эти можно удалить.
# Нужны espeak-ng и ffmpeg.
set -euo pipefail
cd "$(dirname "$0")"
OUT=../../Assets/Sources/Art/Audio/YandexStation/Phrases
mkdir -p "$OUT"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

PHRASES=(
  "Привет! Я Алиса. Чем могу помочь?"
  "Сейчас на корабле тепло и уютно. Снаружи лучше не выходить."
  "Ищу в интернете: как сдать квоту и не умереть."
)

# Звук активации: два мягких тона вверх
ffmpeg -v error -y -f lavfi -i "sine=f=880:d=0.09" -f lavfi -i "sine=f=1320:d=0.14" -filter_complex "\
[0:a]afade=t=out:st=0.05:d=0.04[a];[1:a]afade=t=in:d=0.01,afade=t=out:st=0.06:d=0.08[b];\
[a][b]concat=n=2:v=0:a=1,volume=0.35,aresample=44100,apad=pad_dur=0.12[o]" -map "[o]" "$TMP/chime.wav"

i=1
for text in "${PHRASES[@]}"; do
  espeak-ng -v ru+f3 -s 150 -p 60 -w "$TMP/voice.wav" "$text"
  ffmpeg -v error -y -i "$TMP/chime.wav" -i "$TMP/voice.wav" -filter_complex "\
[1:a]aresample=44100,highpass=f=120[v];\
[0:a][v]concat=n=2:v=0:a=1,loudnorm=I=-16:TP=-2:LRA=11,aresample=44100[o]" \
    -map "[o]" -ac 1 -c:a pcm_s16le "$OUT/Placeholder$i.wav"
  echo "saved $OUT/Placeholder$i.wav"
  i=$((i + 1))
done
