# Тестовые фразы для run.sh: python3 make_audio.py <v4_ru.pt Silero> <папка> phrases.txt
# sNN — Silero (5 голосов по кругу), eNN — espeak-ng, oNN — Silero как через голосовой чат:
# быстрее на 15%, полоса 150–7000 Гц, Opus 16 кбит/с (как Dissonance) и шум ~15 дБ.
# Всё s16le, 48 кГц, моно (.raw) и .wav рядом, чтобы послушать.
import sys, os, subprocess, numpy as np, torch, wave

out = sys.argv[2]
os.makedirs(out, exist_ok=True)
lines = [l.strip() for l in open(sys.argv[3], encoding='utf-8') if l.strip()]
m = torch.package.PackageImporter(sys.argv[1]).load_pickle("tts_models", "model")
m.to('cpu')
speakers = ['aidar', 'eugene', 'baya', 'xenia', 'kseniya']
rng = np.random.default_rng(1)


def save(path, audio):
    pad = np.zeros(int(0.4 * 48000), dtype=np.float32)
    a = np.concatenate([pad, audio.astype(np.float32), pad])
    a = a + rng.normal(0, 0.003, a.shape).astype(np.float32)  # лёгкий шум микрофона
    pcm = (np.clip(a, -1, 1) * 32767).astype('<i2')
    pcm.tofile(path + '.raw')
    with wave.open(path + '.wav', 'wb') as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(48000); w.writeframes(pcm.tobytes())


for i, line in enumerate(lines):
    text = line
    if text.startswith('алиса '):
        text = 'Алиса, ' + text[6:]
    text = text[0].upper() + text[1:]
    text += '?' if any(text.lower().split()[1:2] and w in text.lower() for w in ['где', 'сколько', 'какая', 'кто', 'почему', 'что ты', 'как у', 'слышишь', 'знаешь']) else '.'
    sp = speakers[i % len(speakers)]
    audio = m.apply_tts(text=text, speaker=sp, sample_rate=48000, put_accent=True, put_yo=True).numpy()
    save(os.path.join(out, f's{i:02d}'), audio)
    # espeak-ng: тот же текст роботом
    wav = os.path.join(out, 'tmp.wav')
    subprocess.run(['espeak-ng', '-v', 'ru', '-s', '140', '-w', wav, text], check=True)
    with wave.open(wav) as w:
        rate = w.getframerate(); data = np.frombuffer(w.readframes(w.getnframes()), dtype='<i2').astype(np.float32) / 32768
    t = np.arange(int(len(data) * 48000 / rate)) * rate / 48000
    save(os.path.join(out, f'e{i:02d}'), np.interp(t, np.arange(len(data)), data))
    # Как через голосовой чат
    opus = os.path.join(out, 'tmp.ogg')
    subprocess.run(['ffmpeg', '-v', 'error', '-y', '-f', 's16le', '-ar', '48000', '-ac', '1', '-i', os.path.join(out, f's{i:02d}.raw'),
                    '-af', 'atempo=1.15,highpass=f=150,lowpass=f=7000', '-c:a', 'libopus', '-b:a', '16k', opus], check=True)
    dec = subprocess.run(['ffmpeg', '-v', 'error', '-i', opus, '-f', 's16le', '-ar', '48000', '-ac', '1', '-'], check=True, capture_output=True).stdout
    voice = np.frombuffer(dec, dtype='<i2').astype(np.float32) / 32768
    noise = np.cumsum(rng.normal(0, 1, voice.shape)).astype(np.float32)  # бурый шум (гул)
    noise = noise - np.convolve(noise, np.ones(480) / 480, mode='same')
    noise += rng.normal(0, 0.5, voice.shape).astype(np.float32)
    noise *= np.sqrt(np.mean(voice ** 2)) / np.sqrt(np.mean(noise ** 2)) / 10 ** (15 / 20)
    save(os.path.join(out, f'o{i:02d}'), voice + noise)
    print(i, sp, text, flush=True)
os.remove(os.path.join(out, 'tmp.wav'))
os.remove(os.path.join(out, 'tmp.ogg'))
