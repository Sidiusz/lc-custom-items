#!/bin/sh
# Ищет в собранных DLL мода обращения к private-членам игры (в игре они падают с
# FieldAccessException / MethodAccessException). Такие обращения — через Items/GamePrivate.cs.
# Запуск после `dotnet build -c Release` в CustomItems. protected-конструктор GrabbableObject — норма.
set -e
cd "$(dirname "$0")"
R=$(ls -d ~/.nuget/packages/lethalcompany.gamelibs.steam/*/ref/netstandard2.1 | tail -1)
B=../../CustomItems/bin/Release/netstandard2.1
DIRS=$(find ~/.nuget/packages -name "*.dll" -path "*netstandard*" -printf '%h\n' | sort -u | tr '\n' ' ')
dotnet build -c Release -v q -o out >/dev/null
dotnet out/scan.dll "$R" "$B/SidiuszCustomItems.dll,$B/SidiuszCustomItems.Items.dll" "$B" $DIRS
