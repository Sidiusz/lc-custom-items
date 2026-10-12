#!/usr/bin/env bash
# Звук глотка Егермейстера -> Assets/Sources/Art/Audio/Jagermeister/JagermeisterDrink.wav
# Два глотка из одного звука и спокойный выдох из другого, моно, громкость выровнена.
#
# Исходники с BudgetPixel, лицензия CC BY 4.0 (https://creativecommons.org/licenses/by/4.0/):
#   «Big Gulp Drinking» — BudgetPixel, https://budgetpixel.com/sfx/big-gulp-drinking-9fdd3672
#   «Cold Drink Open Sigh» — BudgetPixel, https://budgetpixel.com/sfx/cold-drink-open-sigh-53076dfd
# Из второго взят только выдох, без звука открывания банки. Нужен ffmpeg.
set -euo pipefail
cd "$(dirname "$0")"
OUT=../../Assets/Sources/Art/Audio/Jagermeister/JagermeisterDrink.wav
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

curl -sSL -o "$TMP/gulp.wav" https://cdn.budgetpixel.com/audio-library/sfx/wet-glugging-gurgly-crisp-closeup-raqcf4.wav
curl -sSL -o "$TMP/sigh.wav" https://cdn.budgetpixel.com/audio-library/sfx/crisp-crack-soft-exhale-faint-1o3i6xk.wav

ffmpeg -v error -y -i "$TMP/gulp.wav" -i "$TMP/sigh.wav" -filter_complex "\
[0:a]atrim=1.18:2.45,asetpts=N/SR/TB,afade=t=in:d=0.03,afade=t=out:st=1.17:d=0.1,pan=mono|c0=0.5*c0+0.5*c1[g];\
[1:a]atrim=1.30:2.80,asetpts=N/SR/TB,afade=t=in:d=0.05,afade=t=out:st=1.15:d=0.35,pan=mono|c0=0.5*c0+0.5*c1,volume=1.6[e];\
[g][e]concat=n=2:v=0:a=1,loudnorm=I=-16:TP=-2:LRA=11,aresample=44100[o]" \
  -map "[o]" -c:a pcm_s16le "$OUT"
echo "saved $OUT"
