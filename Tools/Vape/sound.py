"""
Звуки вейпа -> Assets/Sources/Art/Audio/Vape/ (встраиваются в DLL, грузит EmbeddedAudio).

    python Tools/Vape/sound.py

    VapeInhale.wav   затяжка: шипение испарителя с потрескиванием и воздух, зациклена (2 с)
    VapeExhale.wav   выдох: мягкий поток воздуха (на сколько хватило затяжки, код обрезает громкостью)
    VapeBeep.wav     три коротких писка: защита от перегрева, как у настоящих вейпов
    VapeSizzle.wav   перегрев: громкий треск и шипение, хлопок жидкости
Всё синтез из шума, без записей.
"""
import os
import wave

import numpy as np

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "Assets", "Sources", "Art", "Audio", "Vape")
RATE = 44100
rng = np.random.default_rng(420)


def lowpass(x, cutoff):
    a = np.exp(-2 * np.pi * cutoff / RATE)
    y = np.empty_like(x)
    acc = 0.0
    for i, v in enumerate(x):
        acc = (1 - a) * v + a * acc
        y[i] = acc
    return y


def highpass(x, cutoff):
    return x - lowpass(x, cutoff)


def band(x, lo, hi):
    return lowpass(highpass(x, lo), hi)


def crackle(n, rate_per_s, loud=1.0):
    """Потрескивание: короткие щелчки случайной громкости."""
    out = np.zeros(n)
    count = int(n / RATE * rate_per_s)
    for _ in range(count):
        at = rng.integers(0, n - 400)
        length = rng.integers(40, 300)
        click = rng.normal(0, 1, length) * np.exp(-np.linspace(0, 6, length))
        out[at:at + length] += click * rng.uniform(0.2, 1.0) * loud
    return highpass(out, 1500)


def save(name, x, peak=0.8):
    x = x / (np.max(np.abs(x)) + 1e-9) * peak
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, name)
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes((x * 32767).astype(np.int16).tobytes())
    print(path)


def inhale():
    n = 2 * RATE
    hiss = band(rng.normal(0, 1, n + RATE // 4), 2500, 9000)
    air = band(rng.normal(0, 1, n + RATE // 4), 300, 1800) * 0.6
    x = hiss * 0.45 + air + crackle(n + RATE // 4, 45, 1.6)
    # медленное «дыхание» потока
    t = np.arange(len(x)) / RATE
    x *= 0.85 + 0.15 * np.sin(2 * np.pi * 1.0 * t)
    # шов петли: хвост плавно накладывается на начало
    tail = RATE // 4
    fade = np.linspace(0, 1, tail)
    loop = x[:n].copy()
    loop[:tail] = loop[:tail] * fade + x[n:n + tail] * (1 - fade)
    save("VapeInhale.wav", loop, 0.6)


def exhale():
    n = int(3.0 * RATE)
    t = np.arange(n) / RATE
    env = np.minimum(1, t / 0.12) * np.exp(-t * 1.1)
    breath = band(rng.normal(0, 1, n), 200, 2500)
    # «ффф» губами: немного верхов в начале
    lips = band(rng.normal(0, 1, n), 3000, 7000) * np.exp(-t * 3) * 0.4
    save("VapeExhale.wav", (breath + lips) * env, 0.55)


def beep():
    out = []
    for _ in range(3):
        n = int(0.09 * RATE)
        t = np.arange(n) / RATE
        tone = np.sign(np.sin(2 * np.pi * 3100 * t)) * 0.5 + np.sin(2 * np.pi * 3100 * t) * 0.5
        env = np.minimum(1, np.minimum(t, t[-1] - t) / 0.004)
        out.append(lowpass(tone * env, 9000))
        out.append(np.zeros(int(0.07 * RATE)))
    save("VapeBeep.wav", np.concatenate(out), 0.5)


def sizzle():
    n = int(1.6 * RATE)
    t = np.arange(n) / RATE
    env = np.minimum(1, t / 0.02) * np.exp(-t * 2.2)
    hiss = band(rng.normal(0, 1, n), 1800, 11000) * env
    pops = crackle(n, 160, 2.5) * np.exp(-t * 1.5)
    # хлопок жидкости в начале
    m = int(0.05 * RATE)
    pop = np.zeros(n)
    pop[:m] = lowpass(rng.normal(0, 1, m), 900) * np.exp(-np.linspace(0, 5, m)) * 6
    save("VapeSizzle.wav", hiss + pops + pop, 0.85)


if __name__ == "__main__":
    inhale()
    exhale()
    beep()
    sizzle()
