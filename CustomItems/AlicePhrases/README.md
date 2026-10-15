# Фразы Алисы без Unity

Сюда кладутся новые записи: `<Тема>/<текст>.wav` (или .ogg, .mp3), как в `Assets/.../YandexStation/Phrases`.
Например пак 6: `IsAlive/Yes/Живой!.wav`, `HowDied/Gravity/Упал с высоты.wav`. Сборка копирует папку в
`alice-phrases/` рядом с DLL, мод сам добавляет эти фразы к темам бандла (`LoosePhrases.cs`). Unity не нужен.
