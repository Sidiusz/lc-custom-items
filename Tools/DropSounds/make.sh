#!/usr/bin/env bash
# Звуки падения предметов -> Assets/Sources/Art/Audio/<Предмет>/
#   YandexStation/Sounds/Drop.wav   Freesound 872227 «Soft Impact - Pillow Hit» (KoimeMatsuchiya), удар подушкой
#   Winston/WinstonDrop.wav         BigSoundBank 0576 «Canvas bag on floor», один шлепок тканевой сумки
#   Monitor27/Monitor27Drop.wav     BigSoundBank 2452 «Body fall» чуть выше тоном (тяжёлый глухой удар)
#                                   + первые 60 мс Freesound 697092 «hollow plastic box, dropped» (Lemato),
#                                   щелчок пластикового корпуса без отскоков
#   Jagermeister/JagermeisterDrop.wav  BigSoundBank 0214 «Bottle on a table», бутылку ставят на стол
# Нужны curl и ffmpeg. Результат: моно 44,1 кГц, пик −3 дБ. Freesound часто отвечает 403 на частые запросы,
# тогда подождите и запустите снова.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
audio="$here/../../Assets/Sources/Art/Audio"
tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT

fs() { curl -fsSL --retry 5 --retry-all-errors -o "$tmp/$1.mp3" "https://cdn.freesound.org/previews/${1:0:3}/$1_$2-hq.mp3"; }
bsb() { curl -fsSL --retry 5 --retry-all-errors -o "$tmp/$1.mp3" "https://bigsoundbank.com/UPLOAD/mp3/$1.mp3"; }
# входной wav -> выход с пиком −3 дБ
peak() {
    local gain; gain=$(ffmpeg -i "$1" -af volumedetect -f null - 2>&1 | grep -oE 'max_volume: [-0-9.]+' | awk '{print -3-$2}')
    ffmpeg -v error -y -i "$1" -af "volume=${gain}dB" -c:a pcm_s16le "$audio/$2"
}
# вход, выход, опции до -i, фильтр
make() {
    ffmpeg -v error -y $3 -i "$tmp/$1.mp3" -af "$4" -ac 1 -ar 44100 "$tmp/x.wav"
    peak "$tmp/x.wav" "$2"
}

fs 872227 19256798
fs 697092 6944547
bsb 0576
bsb 2452
bsb 0214

make 872227 YandexStation/Sounds/Drop.wav "" \
    "silenceremove=start_periods=1:start_threshold=-45dB,highpass=f=50,afade=t=in:d=0.003,areverse,silenceremove=start_periods=1:start_threshold=-50dB,areverse,afade=t=out:st=0.2:d=0.1"
make 0576 Winston/WinstonDrop.wav "-ss 3.34 -t 0.4" \
    "highpass=f=50,afade=t=in:d=0.005,afade=t=out:st=0.2:d=0.2"
make 0214 Jagermeister/JagermeisterDrop.wav "-ss 8.22 -t 0.3" \
    "highpass=f=80,afade=t=in:d=0.003,afade=t=out:st=0.15:d=0.15"

# монитор: глухой удар + щелчок пластика поверх, без отскоков
ffmpeg -v error -y -ss 0.26 -t 0.4 -i "$tmp/2452.mp3" -ac 1 \
    -af "asetrate=44100*1.12,aresample=44100,highpass=f=45,afade=t=in:d=0.004,afade=t=out:st=0.15:d=0.2" "$tmp/thud.wav"
ffmpeg -v error -y -i "$tmp/697092.mp3" -ac 1 \
    -af "silenceremove=start_periods=1:start_threshold=-50dB,atrim=0:0.06,afade=t=out:st=0.03:d=0.03,volume=-6dB" "$tmp/click.wav"
ffmpeg -v error -y -i "$tmp/thud.wav" -i "$tmp/click.wav" \
    -filter_complex "[1]adelay=8[c];[0][c]amix=inputs=2:duration=first:normalize=0" "$tmp/monitor.wav"
peak "$tmp/monitor.wav" Monitor27/Monitor27Drop.wav
