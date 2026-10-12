"""
Звуки рвоты и отрыжки от Егермейстера -> CustomItems/Items/Sounds/*.wav
Куски записей с freesound.org (превью), обрезанные и выровненные по громкости. Wav зашиваются
в SidiuszCustomItems.Items.dll (EmbeddedResource), поэтому бандл Unity пересобирать не нужно.

Запуск: python Tools/Jagermeister/vomit_sounds.py [папка-кэша]  (нужны numpy, ffmpeg, curl и интернет;
CDN freesound иногда отвечает 403, тогда повторите позже или положите mp3 в папку кэша)
"""
import os
import subprocess
import sys
import tempfile
import wave

import numpy as np

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "CustomItems", "Items", "Sounds")
RATE = 22050

PREVIEW = "https://cdn.freesound.org/previews/{}/{}-hq.mp3"

# (имя, превью, начало, конец в секундах)
CUTS = [
    ("Vomit1", "370710_4869300", 4.0, 7.3),   # Vomiting on the ground - Remixed
    ("Vomit2", "246308_2833408", 0.0, 1.7),   # Vomiting
    ("Vomit3", "232540_3615882", 4.3, 6.4),   # vomit.wav
    ("Burp1", "338719_5865517", 0.9, 1.5),    # Various Burps
    ("Burp2", "338719_5865517", 4.85, 5.55),
    ("Burp3", "338719_5865517", 6.75, 7.45),
    ("Burp4", "338719_5865517", 8.5, 9.3),
    ("Burp5", "326377_5636408", 0.4, 1.8),    # Burp.wav
]


def load(sound, cache):
    path = os.path.join(cache, sound + ".mp3")
    if not os.path.exists(path):
        url = PREVIEW.format(int(sound.split("_")[0]) // 1000, sound)
        subprocess.run(["curl", "-sSfL", "-o", path, url], check=True)
    raw = subprocess.run(["ffmpeg", "-v", "error", "-i", path, "-ac", "1", "-ar", str(RATE), "-f", "s16le", "-"],
                         capture_output=True, check=True).stdout
    return np.frombuffer(raw, np.int16) / 32768.0


def main():
    os.makedirs(OUT, exist_ok=True)
    cache = sys.argv[1] if len(sys.argv) > 1 else tempfile.mkdtemp()
    for name, sound, start, end in CUTS:
        x = load(sound, cache)[int(start * RATE):int(end * RATE)].copy()
        fade = int(0.03 * RATE)
        x[:fade] *= np.linspace(0, 1, fade)
        x[-fade * 3:] *= np.linspace(1, 0, fade * 3)
        x *= 0.85 / max(np.max(np.abs(x)), 1e-6)
        with wave.open(os.path.join(OUT, name + ".wav"), "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(RATE)
            w.writeframes((x * 32767).astype("<i2").tobytes())
        print(f"{name}: {len(x) / RATE:.2f} s")


if __name__ == "__main__":
    main()
