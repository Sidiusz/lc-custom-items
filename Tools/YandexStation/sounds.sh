#!/usr/bin/env bash
# Сигналы колонки -> Assets/Sources/Art/Audio/YandexStation/Sounds/*.wav
# Синтезированы в духе звуков Алисы (мягкие синусы): настоящие звуки принадлежат Яндексу,
# в мод их не кладём. Свои можно положить поверх с теми же именами.
#   Listen         услышала «Алиса», слушает вопрос (мягкий подъём)
#   ListenTimeout  вопроса не было, перестала слушать (спад)
#   Error          сбой (солнечная вспышка): треск и низкий гудок
#   TurnOn/TurnOff включение и выключение (три ноты вверх / вниз)
# Нужен ffmpeg.
set -euo pipefail
cd "$(dirname "$0")"
OUT=../../Assets/Sources/Art/Audio/YandexStation/Sounds
mkdir -p "$OUT"

# Тон с огибающей: частота, длительность, громкость, задержка (мс)
tone() { echo "sine=f=$1:d=$2,volume=$3,afade=t=in:d=0.008,afade=t=out:st=$(awk "BEGIN{printf \"%.3f\", $2*0.35}"):d=$(awk "BEGIN{printf \"%.3f\", $2*0.65}"),adelay=$4|$4"; }

mix() { # out, фильтры тонов...
  local out=$1; shift
  local inputs="" labels="" i=0
  for f in "$@"; do inputs+="$f[t$i];"; labels+="[t$i]"; i=$((i + 1)); done
  ffmpeg -v error -y -filter_complex "${inputs}${labels}amix=inputs=$i:normalize=0,aresample=44100,apad=pad_dur=0.05,loudnorm=I=-20:TP=-3,aresample=44100[o]" \
    -map "[o]" -ac 1 -c:a pcm_s16le "$OUT/$out.wav"
  echo "saved $OUT/$out.wav"
}

mix Listen "$(tone 784 0.16 0.5 0)" "$(tone 1175 0.22 0.5 90)" "$(tone 1568 0.10 0.12 90)"
mix ListenTimeout "$(tone 1175 0.14 0.5 0)" "$(tone 784 0.24 0.5 90)"
mix TurnOn "$(tone 659 0.12 0.45 0)" "$(tone 880 0.12 0.45 80)" "$(tone 1319 0.26 0.45 160)"
mix TurnOff "$(tone 1319 0.12 0.45 0)" "$(tone 880 0.12 0.45 80)" "$(tone 523 0.30 0.45 160)"

# Ошибка: обрывки шума и прерывистый низкий гудок
ffmpeg -v error -y -filter_complex "\
anoisesrc=d=0.5:c=white:a=0.5,highpass=f=1500,volume='if(lt(mod(t,0.07),0.03),1,0)':eval=frame[n];\
sine=f=150:d=0.5,volume=0.6,volume='if(lt(mod(t,0.12),0.08),1,0.1)':eval=frame,acrusher=bits=4:mix=1[b];\
[n][b]amix=inputs=2:normalize=0,afade=t=out:st=0.4:d=0.1,aresample=44100,loudnorm=I=-18:TP=-3,aresample=44100[o]" \
  -map "[o]" -ac 1 -c:a pcm_s16le "$OUT/Error.wav"
echo "saved $OUT/Error.wav"
