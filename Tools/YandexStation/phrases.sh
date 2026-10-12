#!/usr/bin/env bash
# Временные фразы колонки по темам -> Assets/Sources/Art/Audio/YandexStation/Phrases/<Тема>/Placeholder*.wav
# Синтезатор espeak-ng (женский русский голос). Сигналы (слушает, ошибка, вкл/выкл) делает sounds.sh.
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
  "Weather/Other|Не могу понять, что за погода на этой луне."
  "Error|Ошибка. Нет связи с сервером."
  "Error|Помехи. Повторите. Повторите позже."
)

declare -A count
for entry in "${PHRASES[@]}"; do
  topic=${entry%%|*}
  text=${entry#*|}
  count[$topic]=$(( ${count[$topic]:-0} + 1 ))
  mkdir -p "$OUT/$topic"
  out="$OUT/$topic/Placeholder${count[$topic]}.wav"
  espeak-ng -v ru+f3 -s 150 -p 60 -w "$TMP/voice.wav" "$text"
  fx=""
  # Сбой связи: голос рвётся и хрипит
  [ "$topic" = Error ] && fx="acrusher=bits=6:mix=0.6,volume='if(lt(mod(t,0.23),0.04),0.05,1)':eval=frame,"
  ffmpeg -v error -y -i "$TMP/voice.wav" -filter_complex "\
[0:a]aresample=44100,highpass=f=120,${fx}loudnorm=I=-16:TP=-2:LRA=11,aresample=44100[o]" \
    -map "[o]" -ac 1 -c:a pcm_s16le "$out"
  echo "saved $out"
done
