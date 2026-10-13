#!/bin/sh
# Tools/AliceLLM/run.sh model.gguf [набор] [потоки]
set -e
cd "$(dirname "$0")"
SET=${2:-para}
dotnet build -c Release -v q -o out >/dev/null
NATIVE=$(ls -d ~/.nuget/packages/llamasharp.backend.cpu/0.27.0/LLamaSharpRuntimes/linux-x64/native/avx2)
LD_LIBRARY_PATH="$NATIVE" dotnet out/AliceLLM.dll "$1" ../../CustomItems/intents.txt ../AliceSemantic/tests/$SET.tsv ../AliceSemantic/tests/$SET-off.txt ${3:-4}
