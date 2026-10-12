using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using CustomItems.Items;
using Dissonance.Audio.Capture;
using NAudio.Wave;
using UnityEngine;
using Vosk;

namespace CustomItems
{
    /// <summary>
    /// Распознаёт, что локальный игрок говорит в голосовой чат, пока рядом включённая
    /// Яндекс Станция: офлайн, Vosk с маленькой русской моделью. Распознанный текст
    /// уходит в <see cref="YandexStationItem.OnLocalSpeech"/>, которая и решает,
    /// обращались ли к Алисе.
    ///
    /// Звук берётся у голосового чата игры (Dissonance, подписка на записанный микрофон),
    /// а фраза засчитывается, только если в это время игрока было слышно в чате: с
    /// выключенным микрофоном или не нажатой кнопкой рации (push-to-talk) колонка не
    /// слышит. Каждый игрок распознаёт свою речь у себя, в фоновом потоке, другим по
    /// сети уходит лишь выбранная колонкой фраза. Так колонка слышит всех игроков чата
    /// рядом с собой, а звук чужих голосов никуда не пересылается.
    ///
    /// Vosk лежит рядом с DLL мода: Vosk.dll, папка vosk/ с нативными библиотеками
    /// (Windows x64) и моделью vosk/model-ru/. Всё это кладёт туда сборка (csproj).
    /// </summary>
    public class AliceListener : MonoBehaviour, IMicrophoneSubscriber
    {
        public static string VoskDir;

        /// <summary>
        /// Фразы, которые распознаются (режим грамматики Vosk): с ними маленькая модель почти
        /// не ошибается, всё остальное становится [unk]. null — свободное распознавание.
        /// </summary>
        public static string[] Grammar;

        /// <summary>Фразы по умолчанию: имя и всё, на что отвечает YandexStationItem.Topic.</summary>
        public static readonly string[] DefaultGrammar =
        {
            "алиса",
            "какая погода", "какая погода сейчас", "какая погода на луне", "погода на луне", "что с погодой",
            "погода", "дождь", "туман", "гроза",
            "квота", "сколько до квоты", "мы выполнили квоту", "квота выполнена", "какая квота",
            "сколько дней до сдачи", "сколько дней осталось", "дедлайн", "срок",
            "который час", "сколько времени", "время",
            "привет", "здравствуй", "доброе утро", "добрый день", "добрый вечер",
            "расскажи шутку", "пошути", "расскажи анекдот", "анекдот",
            "включи музыку", "спой песню", "музыка",
            "спасибо", "молодец", "умница",
            "как дела", "как ты",
            "как меня зовут", "кто я", "ты знаешь меня", "чем я занимаюсь",
            "кто тут", "кто здесь", "кто там", "кто ты", "кто это", "кто виноват",
            "какой заряд", "сколько заряда", "заряд", "батарея",
            "какого ты цвета", "твой цвет", "цвет", "хочешь", "будешь",
            "сколько нас", "нас сколько", "сколько нас тут",
            "давай поиграем", "поиграем", "сыграем", "во что поиграем",
            "есть враги", "враги рядом", "тут опасно", "опасно", "монстры", "мне скучно", "скучно",
            "ты меня любишь", "любишь меня", "я тебе нравлюсь",
            "сделай", "принеси", "помоги", "открой", "потанцуй", "найди",
            "хорошо", "ладно", "понятно", "ясно", "я", "мы", "мне",
            "пока", "до свидания", "до встречи", "прощай", "увидимся",
            "стоп", "хватит", "замолчи", "тихо", "выключись",
        };

        private static readonly Regex TextField = new Regex("\"text\"\\s*:\\s*\"([^\"]*)\"");

        private object subscribedTo;
        private float lastHeardInChat = -100f;

        // Фраза распознаётся после паузы, поэтому смотрим, звучал ли игрок в чате недавно
        private const float HeardWindow = 3f;
        private volatile bool active;
        private volatile bool stopping;
        private volatile bool failed;

        private readonly object gate = new object();
        private readonly Queue<short[]> chunks = new Queue<short[]>();
        private readonly Queue<string> results = new Queue<string>();
        private int sampleRate;
        private Thread worker;

        private void Update()
        {
            if (failed)
                return;

            // Голосовой чат пересоздаётся при каждом входе в лобби
            var comms = StartOfRound.Instance != null ? StartOfRound.Instance.voiceChatModule : null;
            if (comms != null && !ReferenceEquals(comms, subscribedTo))
            {
                comms.SubscribeToRecordedAudio(this);
                subscribedTo = comms;
            }

            active = comms != null && !comms.IsMuted && YandexStationItem.AnyNearLocalPlayer();
            if (active)
            {
                var me = comms.LocalPlayerName != null ? comms.FindPlayer(comms.LocalPlayerName) : null;
                if (me != null && me.IsSpeaking)
                    lastHeardInChat = Time.time;
            }
            if (active && worker == null)
                StartWorker();

            while (true)
            {
                string text;
                lock (gate)
                {
                    if (results.Count == 0)
                        break;
                    text = results.Dequeue();
                }
                if (Time.time - lastHeardInChat > HeardWindow)
                    continue; // говорил мимо чата (push-to-talk не нажат)
                Plugin.Log.LogDebug("Алиса услышала: " + text);
                YandexStationItem.OnLocalSpeech(text);
            }
        }

        private void OnDestroy()
        {
            stopping = true;
            lock (gate)
                Monitor.PulseAll(gate);
        }

        // Вызывает Dissonance (может быть не с главного потока): только копируем звук в очередь
        public void ReceiveMicrophoneData(ArraySegment<float> buffer, WaveFormat format)
        {
            if (!active || buffer.Array == null)
                return;
            var pcm = new short[buffer.Count];
            for (var i = 0; i < buffer.Count; i++)
                pcm[i] = (short)Mathf.Clamp(buffer.Array[buffer.Offset + i] * 32767f, -32768f, 32767f);
            lock (gate)
            {
                sampleRate = format.SampleRate;
                if (chunks.Count < 200) // ~4 с при кадрах по 20 мс; если не успеваем, старое выбрасываем
                    chunks.Enqueue(pcm);
                Monitor.Pulse(gate);
            }
        }

        public void Reset()
        {
            lock (gate)
                chunks.Clear();
        }

        private void StartWorker()
        {
            worker = new Thread(Work) { IsBackground = true, Name = "SidiuszCustomItems.Alice" };
            worker.Start();
        }

        private void Work()
        {
            Model model;
            try
            {
                LoadNatives();
                Vosk.Vosk.SetLogLevel(-1);
                model = new Model(Path.Combine(VoskDir, "model-ru"));
                Plugin.Log.LogInfo("Алиса: модель распознавания речи загружена");
            }
            catch (Exception e)
            {
                failed = true;
                Plugin.Log.LogError("Алиса: не удалось загрузить Vosk, голосовые команды выключены. " + e);
                return;
            }

            VoskRecognizer recognizer = null;
            var rate = 0;
            while (!stopping)
            {
                short[] pcm;
                lock (gate)
                {
                    while (chunks.Count == 0 && !stopping)
                        Monitor.Wait(gate, 500);
                    if (stopping)
                        break;
                    pcm = chunks.Dequeue();
                    if (rate != sampleRate)
                    {
                        rate = sampleRate;
                        recognizer?.Dispose();
                        recognizer = Grammar != null && Grammar.Length > 0
                            ? new VoskRecognizer(model, rate, GrammarJson())
                            : new VoskRecognizer(model, rate);
                    }
                }

                try
                {
                    if (!recognizer.AcceptWaveform(pcm, pcm.Length))
                        continue;
                    var match = TextField.Match(recognizer.Result());
                    if (match.Success && match.Groups[1].Value.Length > 0)
                    {
                        lock (gate)
                            results.Enqueue(match.Groups[1].Value);
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Алиса: ошибка распознавания: " + e.Message);
                }
            }
            recognizer?.Dispose();
            model.Dispose();
        }

        private static string GrammarJson()
        {
            string[] custom;
            lock (YandexStationItem.CustomTopics)
                custom = YandexStationItem.CustomTopics.ToArray();
            var phrases = new List<string>();
            foreach (var p in Grammar.Concat(custom))
            {
                var t = p.Trim().ToLowerInvariant().Replace("\"", "");
                if (t.Length > 0)
                    phrases.Add("\"" + t + "\"");
            }
            phrases.Add("\"[unk]\"");
            return "[" + string.Join(", ", phrases) + "]";
        }

        // Mono ищет libvosk.dll рядом с игрой, а не рядом с модом: загружаем её
        // и зависимости заранее по полному пути, дальше DllImport найдёт уже загруженную.
        private static void LoadNatives()
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer)
                return;
            foreach (var name in new[] { "libwinpthread-1.dll", "libgcc_s_seh-1.dll", "libstdc++-6.dll", "libvosk.dll" })
            {
                var path = Path.Combine(VoskDir, name);
                if (LoadLibraryW(path) == IntPtr.Zero)
                    throw new FileNotFoundException($"Не загрузилась {path} (ошибка {Marshal.GetLastWin32Error()})");
            }
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);
    }
}
