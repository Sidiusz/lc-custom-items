#!/usr/bin/env bash
# Звуки падения колонки, Уинстона и монитора. Исходники с Freesound, все CC0:
#   872227 «Soft Impact - Pillow Hit», KoimeMatsuchiya      → YandexStation/Sounds/Drop.wav
#   627330 «Stuffed Animal Toy Impact», seema_grewal        → Winston/WinstonDrop.wav (удар на 31,85 с)
#   697092 «hollow plastic box, dropped», Lemato            → Monitor27/Monitor27Drop.wav (чуть ниже тоном)
# Нужны curl и ffmpeg. Результат: моно 44,1 кГц, пик −3 дБ.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
audio="$here/../../Assets/Sources/Art/Audio"
tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT

get() { curl -fsSL --retry 5 --retry-all-errors -o "$tmp/$1.mp3" "https://cdn.freesound.org/previews/${1:0:3}/$1_$2-hq.mp3"; }
make() { # вход, выход, опции до -i, фильтр
    ffmpeg -v error -y $3 -i "$tmp/$1.mp3" -af "$4" -ac 1 -ar 44100 "$tmp/x.wav"
    local peak; peak=$(ffmpeg -i "$tmp/x.wav" -af volumedetect -f null - 2>&1 | grep -oE 'max_volume: [-0-9.]+' | awk '{print -3-$2}')
    ffmpeg -v error -y -i "$tmp/x.wav" -af "volume=${peak}dB" -c:a pcm_s16le "$audio/$2"
}

get 872227 19256798
get 627330 11920353
get 697092 6944547

make 872227 YandexStation/Sounds/Drop.wav "" \
    "silenceremove=start_periods=1:start_threshold=-45dB,highpass=f=50,afade=t=in:d=0.003,areverse,silenceremove=start_periods=1:start_threshold=-50dB,areverse,afade=t=out:st=0.2:d=0.1"
make 627330 Winston/WinstonDrop.wav "-ss 31.85 -t 0.35" \
    "highpass=f=60,afade=t=in:d=0.005,afade=t=out:st=0.15:d=0.2"
make 697092 Monitor27/Monitor27Drop.wav "" \
    "silenceremove=start_periods=1:start_threshold=-50dB,asetrate=44100*0.88,aresample=44100,atrim=0:0.24,afade=t=in:d=0.003,afade=t=out:st=0.12:d=0.12"
