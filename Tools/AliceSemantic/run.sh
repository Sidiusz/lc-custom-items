#!/bin/sh
# Проверка понимания Алисы по смыслу на Linux (те же AliceSemantic.cs и AliceIntents.cs, что в моде).
#   Tools/AliceSemantic/run.sh [a|b|c] [intents.txt]
# Наборы tests/<x>.tsv («вопрос<TAB>тема») и tests/<x>-off.txt (фразы не к колонке, темы быть не должно).
# Пороги подбирались на a и b, c — проверочный. Печатает точность по словам / по смыслу / вместе,
# ложные срабатывания и задержку. VERBOSE=1 — каждый вопрос; SEM_IntentThreshold=0.85 — поменять поле.
# Модель — CustomItems/AliceSemantic/*.gguf, сделать заново: bert2gguf.py (там же написано как).
set -e
cd "$(dirname "$0")"
SET=${1:-a}
INTENTS=${2:-../../CustomItems/intents.txt}
dotnet build Harness -c Release -v q -o out >/dev/null
NATIVE=$(ls -d ~/.nuget/packages/llamasharp.backend.cpu/0.27.0/LLamaSharpRuntimes/linux-x64/native/avx2)
mkdir -p /tmp/alice-semantic
LD_LIBRARY_PATH="$NATIVE" dotnet out/Harness.dll ../../CustomItems/AliceSemantic/rubert-mini-frida-f16.gguf "$INTENTS" \
    ../../Assets/Sources/Art/Audio/YandexStation/Phrases "tests/$SET.tsv" "tests/$SET-off.txt" /tmp/alice-semantic/cache.bin $DUMP
