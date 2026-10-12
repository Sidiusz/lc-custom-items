"""
Звук глотка Егермейстера -> Assets/Sources/Art/Audio/Jagermeister/JagermeisterDrink.wav
Просто выдох после глотка: шум, окрашенный под шёпотное «ха-а», без голоса. Собирается целиком
из шума, чтобы в звуке не было никаких голосовых призвуков. Нужен numpy.

Клип играет через 0.3 с после начала питья (JagermeisterItem.SoundDelay), бутылку опускают
на 1.4 с, поэтому выдох начинается с паузой LEAD, когда бутылка уже ото рта.

Запуск: python Tools/Jagermeister/sound.py
"""
import os
import wave

import numpy as np

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "Assets", "Sources", "Art", "Audio", "Jagermeister", "JagermeisterDrink.wav")

RATE = 44100
LEAD = 0.95     # пауза до выдоха, с
BREATH = 1.1    # длина выдоха, с


def breath(rng):
    n = int(BREATH * RATE)
    noise = rng.standard_normal(n)
    spec = np.fft.rfft(noise)
    f = np.fft.rfftfreq(n, 1 / RATE)

    def peak(fc, bw, gain):
        return gain * np.exp(-0.5 * ((f - fc) / bw) ** 2)

    # шёпотное «ха»: широкие форманты, мягкий спад верхов, без низа
    shape = (peak(650, 260, 1.0) + peak(1150, 380, 0.75) + peak(2500, 700, 0.35)
             + 0.12 / (1 + (f / 4000) ** 2))
    shape *= 1 / (1 + (120 / np.maximum(f, 1)) ** 4)
    x = np.fft.irfft(spec * shape, n)

    t = np.arange(n) / RATE
    # быстрый вдох силы, потом долгое затухание; лёгкая неровность струи
    env = np.minimum(t / 0.07, 1) * np.exp(-np.maximum(t - 0.12, 0) * 3.2)
    env *= 1 + 0.12 * np.sin(2 * np.pi * 5.5 * t + 1.3)
    env[-int(0.15 * RATE):] *= np.linspace(1, 0, int(0.15 * RATE))
    x *= env
    return x


def main():
    rng = np.random.default_rng(1337)
    x = np.concatenate([np.zeros(int(LEAD * RATE)), breath(rng)])
    x *= 0.5 / np.max(np.abs(x))
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with wave.open(OUT, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes((x * 32767).astype("<i2").tobytes())
    print(f"saved {OUT}")


if __name__ == "__main__":
    main()
