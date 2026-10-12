"""
Звуки энергетика -> Assets/Sources/Art/Audio/Adrenaline/
  AdrenalineOpen.wav       первый глоток: банку открывают (щелчок, шипение) и пьют
  AdrenalineSip.wav        следующие глотки
  AdrenalineHeartbeat.wav  один удар сердца «тук-тук», код повторяет его с нужным темпом

Клипы режутся из записей с Freesound (CC0), превью скачиваются заново при каждом запуске:
  открывание: michorvath, «Opening a can of soda»  https://freesound.org/s/386887/
  глотки:     nataliedmc, «Gulping»                https://freesound.org/s/465405/
  сердце:     Cloud-10,   «Heartbeat 97 BPM»       https://freesound.org/s/688735/

WAV встраиваются в SidiuszCustomItems.Items.dll (см. CustomItems.Items.csproj), бандл
пересобирать не нужно. Нужны numpy и ffmpeg.

Запуск: python Tools/Adrenaline/sound.py
"""
import os
import subprocess
import tempfile
import urllib.request
import wave

import numpy as np

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "Assets", "Sources", "Art", "Audio", "Adrenaline")
RATE = 44100

SOURCES = {
    "open": "https://cdn.freesound.org/previews/386/386887_3094998-hq.mp3",
    "gulp": "https://cdn.freesound.org/previews/465/465405_9677703-hq.mp3",
    "heart": "https://cdn.freesound.org/previews/688/688735_10403690-hq.mp3",
}

# Куски записей, секунды
CAN_OPEN = (0.18, 0.80)                                          # щелчок язычка и шипение
GULPS = [(0.80, 1.06), (1.45, 1.66), (2.18, 2.40), (2.90, 3.13)]  # отдельные глотки
HEARTBEAT = (1.15, 1.85)                                         # «тук-тук»


def load(url, tmp):
    mp3 = os.path.join(tmp, os.path.basename(url))
    req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
    with urllib.request.urlopen(req, timeout=60) as r, open(mp3, "wb") as f:
        f.write(r.read())
    raw = subprocess.run(["ffmpeg", "-loglevel", "error", "-i", mp3, "-ac", "1", "-ar", str(RATE),
                          "-f", "s16le", "-"], check=True, capture_output=True).stdout
    return np.frombuffer(raw, np.int16).astype(np.float64) / 32768


def cut(x, span, fade=0.012):
    a = x[int(span[0] * RATE):int(span[1] * RATE)].copy()
    n = int(fade * RATE)
    ramp = np.linspace(0, 1, n)
    a[:n] *= ramp
    a[-n:] *= ramp[::-1]
    return a


def place(parts, length):
    """parts: [(секунда начала, кусок)]"""
    out = np.zeros(int(length * RATE))
    for start, a in parts:
        s = int(start * RATE)
        out[s:s + len(a)] += a[:len(out) - s]
    return out


def normalize(x, peak):
    return x * (peak / np.abs(x).max())


def save(name, x):
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, name)
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes((np.clip(x, -1, 1) * 32767).astype("<i2").tobytes())
    print(path, f"{len(x) / RATE:.2f} с")


def main():
    with tempfile.TemporaryDirectory() as tmp:
        src = {k: load(u, tmp) for k, u in SOURCES.items()}

    gulps = [normalize(cut(src["gulp"], g), 0.8) for g in GULPS]
    can = normalize(cut(src["open"], CAN_OPEN), 0.9)

    # Открыл, пауза, два глотка
    save("AdrenalineOpen.wav", place([(0.0, can), (0.85, gulps[1]), (1.3, gulps[3])], 1.6))
    # Три глотка подряд
    save("AdrenalineSip.wav", place([(0.05, gulps[0]), (0.45, gulps[2]), (0.85, gulps[3])], 1.15))
    save("AdrenalineHeartbeat.wav", normalize(cut(src["heart"], HEARTBEAT, 0.004), 0.9))


if __name__ == "__main__":
    main()
