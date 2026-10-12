#!/usr/bin/env bash
# Временные фразы колонки по темам -> Assets/Sources/Art/Audio/YandexStation/Phrases/<Тема>/Placeholder*.wav
# Синтезатор espeak-ng (женский русский голос) и короткий звук «Алиса слушает» перед фразой.
# Это заглушки: свои записи кладите в те же папки (любые .wav, сколько угодно на тему),
# а эти удалите. Тему выбирает YandexStationItem.Topic, список тем в README.
# Нужны espeak-ng и ffmpeg.
set -euo pipefail
cd "$(dirname "$0")"
OUT=../../Assets/Sources/Art/Audio/YandexStation/Phrases
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

# Тема|фраза
PHRASES=(
  "Random|Привет! Я Алиса. Чем могу помочь?"
  "Random|Сейчас на корабле тепло и уютно. Снаружи лучше не выходить."
  "Random|Ищу в интернете: как сдать квоту и не умереть."
  "Listen|Да?"
  "Listen|Слушаю."
  "Unknown|Извините, я не поняла."
  "Unknown|Я пока так не умею."
  "Greeting|Привет! Рада вас слышать."
  "Thanks|Всегда пожалуйста."
  "HowAreYou|Отлично! Особенно когда вы возвращаетесь живыми."
  "Joke|Почему сотрудники Компании не боятся темноты? Потому что они боятся квоты."
  "Time|Время на экране."
  "QuotaDone|Квота выполнена. Можно выдохнуть."
  "QuotaNotDone|Квота ещё не выполнена. Работаем."
  "Deadline0|Сегодня последний день. Пора сдавать квоту."
  "Deadline1|До сдачи квоты остался один день."
  "Deadline2|До сдачи квоты осталось два дня."
  "Deadline3|До сдачи квоты осталось три дня."
  "Weather/Clear|На луне ясно. Отличный день для работы."
  "Weather/DustClouds|Пылевые облака. Видимость плохая."
  "Weather/Rainy|Идёт дождь. Осторожнее с зыбучими песками."
  "Weather/Stormy|Гроза. Не держите металл в руках."
  "Weather/Foggy|Густой туман. Держитесь вместе."
  "Weather/Flooded|Луну затопило, вода будет подниматься."
  "Weather/Eclipsed|Затмение. Снаружи очень опасно."
)

# Звук активации: два мягких тона вверх
ffmpeg -v error -y -f lavfi -i "sine=f=880:d=0.09" -f lavfi -i "sine=f=1320:d=0.14" -filter_complex "\
[0:a]afade=t=out:st=0.05:d=0.04[a];[1:a]afade=t=in:d=0.01,afade=t=out:st=0.06:d=0.08[b];\
[a][b]concat=n=2:v=0:a=1,volume=0.35,aresample=44100,apad=pad_dur=0.12[o]" -map "[o]" "$TMP/chime.wav"

declare -A count
for entry in "${PHRASES[@]}"; do
  topic=${entry%%|*}
  text=${entry#*|}
  count[$topic]=$(( ${count[$topic]:-0} + 1 ))
  mkdir -p "$OUT/$topic"
  out="$OUT/$topic/Placeholder${count[$topic]}.wav"
  espeak-ng -v ru+f3 -s 150 -p 60 -w "$TMP/voice.wav" "$text"
  ffmpeg -v error -y -i "$TMP/chime.wav" -i "$TMP/voice.wav" -filter_complex "\
[1:a]aresample=44100,highpass=f=120[v];\
[0:a][v]concat=n=2:v=0:a=1,loudnorm=I=-16:TP=-2:LRA=11,aresample=44100[o]" \
    -map "[o]" -ac 1 -c:a pcm_s16le "$out"
  echo "saved $out"
done
