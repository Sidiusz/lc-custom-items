"""Озвучивает фразы колонки из phrases.txt
-> Assets/Sources/Art/Audio/YandexStation/Phrases/<Тема>/Alice<N>.wav

Синтез Silero TTS (v4_ru, женский голос xenia), офлайн на процессоре.
Свои записи можно класть в те же папки (любые .wav), сгенерированные удалять.

    pip install torch --index-url https://download.pytorch.org/whl/cpu
    python phrases.py [голос] [тема]  # голос xenia (по умолчанию), baya, kseniya;
                                      # тема: озвучить только темы с этого начала, например Events/

Нужен ffmpeg (подрезка тишины, громкость, 44.1 кГц моно). Тема Error
дополнительно рвётся и хрипит, как связь при солнечной вспышке.
Модель (40 МБ) скачивается один раз в ~/.cache/silero.
"""
import os
import subprocess
import sys
import tempfile
import urllib.request

import torch

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "../../Assets/Sources/Art/Audio/YandexStation/Phrases")
MODEL_URL = "https://models.silero.ai/models/tts/ru/v4_ru.pt"
SPEAKER = sys.argv[1] if len(sys.argv) > 1 else "xenia"
ONLY = sys.argv[2] if len(sys.argv) > 2 else ""

cache = os.path.expanduser("~/.cache/silero/v4_ru.pt")
if not os.path.exists(cache):
    os.makedirs(os.path.dirname(cache), exist_ok=True)
    urllib.request.urlretrieve(MODEL_URL, cache)
model = torch.package.PackageImporter(cache).load_pickle("tts_models", "model")
model.to("cpu")

phrases = []
with open(os.path.join(HERE, "phrases.txt"), encoding="utf-8") as f:
    for line in f:
        line = line.strip()
        if line and not line.startswith("#"):
            topic, text = line.split("|", 1)
            if topic.strip().startswith(ONLY):
                phrases.append((topic.strip(), text.strip()))

# Старые сгенерированные файлы заменяем целиком, свои записи не трогаем
for root, _, files in os.walk(os.path.join(OUT, ONLY) if ONLY else OUT):
    for name in files:
        if name.startswith(("Alice", "Placeholder")) and name.endswith((".wav", ".wav.meta")):
            os.remove(os.path.join(root, name))

count = {}
with tempfile.TemporaryDirectory() as tmp:
    raw = os.path.join(tmp, "raw.wav")
    for topic, text in phrases:
        count[topic] = count.get(topic, 0) + 1
        folder = os.path.join(OUT, topic)
        os.makedirs(folder, exist_ok=True)
        out = os.path.join(folder, f"Alice{count[topic]}.wav")
        ssml = f'<speak><prosody rate="medium">{text}</prosody></speak>'
        model.save_wav(ssml_text=ssml, speaker=SPEAKER, sample_rate=48000, audio_path=raw)
        fx = ""
        if topic == "Error":
            fx = "acrusher=bits=6:mix=0.6,volume='if(lt(mod(t,0.23),0.04),0.05,1)':eval=frame,"
        subprocess.run([
            "ffmpeg", "-v", "error", "-y", "-i", raw, "-af",
            "silenceremove=start_periods=1:start_threshold=-50dB,areverse,"
            "silenceremove=start_periods=1:start_threshold=-50dB,areverse,"
            f"highpass=f=90,{fx}loudnorm=I=-16:TP=-2:LRA=11,aresample=44100,apad=pad_dur=0.1",
            "-ac", "1", "-c:a", "pcm_s16le", out], check=True)
        print("saved", os.path.relpath(out, HERE))
