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
    /// Распознаёт, что игроки говорят в голосовой чат рядом с включённой Яндекс Станцией:
    /// офлайн, Vosk с маленькой русской моделью. Текст уходит в
    /// <see cref="YandexStationItem.OnSpeech"/>, которая и решает, обращались ли к Алисе.
    ///
    /// По умолчанию (<see cref="HostListens"/>) слушает только хост, и сразу всех: свой
    /// микрофон (подписка Dissonance) и голоса остальных, которые и так приходят к нему по
    /// голосовому чату (фильтр на AudioSource голоса каждого игрока). Так колонка понимает
    /// любого игрока, даже если у него распознавание не завелось, а ответ один на всех.
    /// Иначе — по-старому: каждый распознаёт свой микрофон у себя.
    ///
    /// Звук идёт в распознавание, только пока игрока слышно в чате (push-to-talk нажат) и
    /// ещё чуть-чуть после, и только рядом с колонкой. У каждого игрока свой распознаватель.
    /// Ответ приходит не после паузы, а как только в куске фразы есть «Алиса» и понятный
    /// вопрос (два одинаковых промежуточных результата подряд).
    ///
    /// Vosk лежит рядом с DLL мода: Vosk.dll, папка vosk/ с нативными библиотеками
    /// (Windows x64) и моделью vosk/model-ru/. Всё это кладёт туда сборка (csproj).
    /// </summary>
    public class AliceListener : MonoBehaviour, IMicrophoneSubscriber
    {
        public static string VoskDir;

        /// <summary>Распознаёт хост голоса всех игроков (true) или каждый свой микрофон (false).</summary>
        public static bool HostListens = true;

        /// <summary>
        /// Фразы, которые распознаются (режим грамматики Vosk): с ними маленькая модель почти
        /// не ошибается, всё остальное становится [unk]. null — свободное распознавание.
        /// Собирается из примеров intents.txt (<see cref="AliceIntents"/>): фразы целиком и каждое
        /// слово отдельно, так разбираются и вопросы своими словами («алиса а что там с погодой»).
        /// </summary>
        public static string[] Grammar;

        private static readonly Regex TextField = new Regex("\"text\"\\s*:\\s*\"([^\"]*)\"");
        private static readonly Regex PartialField = new Regex("\"partial\"\\s*:\\s*\"([^\"]*)\"");

        // После конца речи в чате ещё столько секунд кормим распознавание (оно ждёт паузу)
        private const float Tail = 1.2f;
        // Промежуточный результат спрашиваем не чаще, чем раз в столько звука
        private const float PartialEvery = 0.2f;
        private const int Mic = -1;

        private object subscribedTo;
        private volatile bool stopping;
        private volatile bool failed;

        private readonly object gate = new object();
        private readonly Queue<Chunk> chunks = new Queue<Chunk>();
        private readonly Queue<Heard> results = new Queue<Heard>();
        private readonly HashSet<int> resetRequests = new HashSet<int>();
        private Thread worker;

        // Кто сейчас слушается: игрок → до какого времени (говорил + хвост)
        private readonly Dictionary<int, float> hearUntil = new Dictionary<int, float>();
        private readonly Dictionary<int, string> lastPartial = new Dictionary<int, string>();
        private readonly HashSet<int> answered = new HashSet<int>();
        private readonly HashSet<VoiceTap> taps = new HashSet<VoiceTap>(), touchedTaps = new HashSet<VoiceTap>();
        private volatile bool micOn;
        private volatile int micPlayer;
        private int outputRate;

        internal struct Chunk
        {
            public int Player, Rate;
            public short[] Pcm; // null — игрок замолчал, дожать фразу
        }

        internal struct Heard
        {
            public int Player;
            public string Text; // null — распознаватель сброшен после ответа на кусок фразы
            public bool Partial;
        }

        private void Awake() =>
            YandexStationItem.ResetHearing = ResetPlayer;

        /// <summary>
        /// Колонка услышала шаг разговора («Алиса», вопрос) или говорит: всё, что игрок наговорил
        /// до сих пор, выбрасываем, чтобы следующая фраза не склеилась с прошлой.
        /// </summary>
        private void ResetPlayer(GameNetcodeStuff.PlayerControllerB player)
        {
            var round = StartOfRound.Instance;
            if (round == null || player == null)
                return;
            var index = Array.IndexOf(round.allPlayerScripts, player);
            if (index < 0)
                return;
            lastPartial.Remove(index);
            answered.Add(index); // остаток этой фразы (финальный результат) не отвечаем
            lock (gate)
                resetRequests.Add(index);
        }

        private void Update()
        {
            if (failed)
                return;
            outputRate = AudioSettings.outputSampleRate;

            // Голосовой чат пересоздаётся при каждом входе в лобби
            var round = StartOfRound.Instance;
            var comms = round != null ? round.voiceChatModule : null;
            if (comms != null && !ReferenceEquals(comms, subscribedTo))
            {
                comms.SubscribeToRecordedAudio(this);
                subscribedTo = comms;
            }

            var nm = Unity.Netcode.NetworkManager.Singleton;
            var listen = comms != null && round.allPlayerScripts != null
                         && (!HostListens || nm != null && nm.IsServer);
            var any = false;
            touchedTaps.Clear();
            if (listen)
            {
                var local = GameNetworkManager.Instance?.localPlayerController;
                for (var i = 0; i < round.allPlayerScripts.Length; i++)
                {
                    var p = round.allPlayerScripts[i];
                    if (p == null)
                        continue;
                    var isLocal = p == local;
                    if (!isLocal && !HostListens)
                        continue;
                    var state = isLocal
                        ? comms.LocalPlayerName != null ? comms.FindPlayer(comms.LocalPlayerName) : null
                        : p.voicePlayerState;
                    var speaking = state != null && state.IsSpeaking && (!isLocal || !comms.IsMuted);
                    if (speaking && YandexStationItem.NearPlayer(p))
                        hearUntil[i] = Time.time + Tail;
                    var on = hearUntil.TryGetValue(i, out var until) && Time.time < until;
                    if (!on && hearUntil.Remove(i))
                        Push(new Chunk { Player = i }); // замолчал: пусть распознавание закончит фразу
                    any |= on;

                    if (isLocal)
                    {
                        micPlayer = i;
                        micOn = on;
                    }
                    else if (p.currentVoiceChatAudioSource != null)
                    {
                        var tap = p.currentVoiceChatAudioSource.GetComponent<VoiceTap>()
                                  ?? p.currentVoiceChatAudioSource.gameObject.AddComponent<VoiceTap>();
                        tap.Listener = this;
                        tap.Player = i;
                        tap.Rate = outputRate;
                        tap.On = on;
                        touchedTaps.Add(tap);
                        taps.Add(tap);
                    }
                }
            }
            else
            {
                micOn = false;
                hearUntil.Clear();
            }
            // Голос перешёл к другому игроку: старый тап молчит, иначе слал бы звук под чужим именем
            taps.RemoveWhere(t => t == null);
            foreach (var tap in taps)
            {
                if (!touchedTaps.Contains(tap))
                    tap.On = false;
            }
            if (any && worker == null)
                StartWorker();

            while (true)
            {
                Heard heard;
                lock (gate)
                {
                    if (results.Count == 0)
                        break;
                    heard = results.Dequeue();
                }
                Handle(heard, round);
            }
        }

        private void Handle(Heard heard, StartOfRound round)
        {
            if (heard.Text == null)
            {
                answered.Remove(heard.Player); // распознаватель сброшен, дальше новая фраза
                return;
            }
            var player = round != null && heard.Player >= 0 && heard.Player < round.allPlayerScripts.Length
                ? round.allPlayerScripts[heard.Player] : null;
            if (player == null)
                return;

            // Слышны слова — человек ещё говорит: колонка подождёт с выключением
            if (heard.Text.Length > 0)
                YandexStationItem.OnVoice(player);

            if (heard.Partial)
            {
                // Ответить можно уже по куску: «алиса какая погода» дважды подряд — дальше не ждём
                lastPartial.TryGetValue(heard.Player, out var before);
                lastPartial[heard.Player] = heard.Text;
                if (answered.Contains(heard.Player) || heard.Text != before || !YandexStationItem.IsCompleteCommand(heard.Text))
                    return;
                answered.Add(heard.Player);
                lock (gate)
                    resetRequests.Add(heard.Player);
            }
            else
            {
                lastPartial.Remove(heard.Player);
                if (answered.Contains(heard.Player))
                    return; // на эту фразу уже ответили по куску
            }
            Plugin.Log.LogDebug($"Алиса услышала {player.playerUsername}: {heard.Text}");
            YandexStationItem.OnSpeech(player, heard.Text);
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
            if (!micOn || buffer.Array == null)
                return;
            var pcm = new short[buffer.Count];
            for (var i = 0; i < buffer.Count; i++)
                pcm[i] = ToShort(buffer.Array[buffer.Offset + i]);
            Push(new Chunk { Player = micPlayer, Rate = format.SampleRate, Pcm = pcm });
        }

        public void Reset()
        {
        }

        internal static short ToShort(float v) => (short)Mathf.Clamp(v * 32767f, -32768f, 32767f);

        internal void Push(Chunk chunk)
        {
            lock (gate)
            {
                if (chunks.Count < 400) // ~4 с на двоих при кадрах по 20 мс; если не успеваем, новое выбрасываем
                    chunks.Enqueue(chunk);
                Monitor.Pulse(gate);
            }
        }

        /// <summary>
        /// Голос другого игрока, как его слышит хост: висит на AudioSource голоса после
        /// Dissonance и забирает первый канал. Звук не меняет.
        /// </summary>
        public class VoiceTap : MonoBehaviour
        {
            internal AliceListener Listener;
            internal volatile bool On;
            internal int Player, Rate;

            private void OnAudioFilterRead(float[] data, int channels)
            {
                var listener = Listener;
                if (!On || listener == null || Rate <= 0 || channels <= 0)
                    return;
                var pcm = new short[data.Length / channels];
                for (var i = 0; i < pcm.Length; i++)
                    pcm[i] = ToShort(data[i * channels]);
                listener.Push(new Chunk { Player = Player, Rate = Rate, Pcm = pcm });
            }
        }

        private void StartWorker()
        {
            worker = new Thread(Work) { IsBackground = true, Name = "SidiuszCustomItems.Alice" };
            worker.Start();
        }

        private class Stream
        {
            public VoskRecognizer Recognizer;
            public int Rate;
            public int SinceSamples;
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

            var grammar = Grammar != null && Grammar.Length > 0 ? GrammarJson() : null;
            var streams = new Dictionary<int, Stream>();
            while (!stopping)
            {
                Chunk chunk;
                bool reset;
                lock (gate)
                {
                    while (chunks.Count == 0 && !stopping)
                        Monitor.Wait(gate, 500);
                    if (stopping)
                        break;
                    chunk = chunks.Dequeue();
                    reset = resetRequests.Remove(chunk.Player);
                }

                try
                {
                    streams.TryGetValue(chunk.Player, out var stream);
                    if (reset && stream != null)
                    {
                        stream.Recognizer.Reset();
                        Emit(chunk.Player, null, false);
                    }
                    if (chunk.Pcm == null)
                    {
                        // Игрок замолчал: дожимаем фразу
                        if (stream != null && !reset)
                            Emit(chunk.Player, Text(TextField, stream.Recognizer.FinalResult()), false);
                        continue;
                    }
                    if (stream == null || stream.Rate != chunk.Rate)
                    {
                        stream?.Recognizer.Dispose();
                        stream = new Stream
                        {
                            Rate = chunk.Rate,
                            Recognizer = grammar != null
                                ? new VoskRecognizer(model, chunk.Rate, grammar)
                                : new VoskRecognizer(model, chunk.Rate),
                        };
                        streams[chunk.Player] = stream;
                    }

                    if (stream.Recognizer.AcceptWaveform(chunk.Pcm, chunk.Pcm.Length))
                    {
                        stream.SinceSamples = 0;
                        Emit(chunk.Player, Text(TextField, stream.Recognizer.Result()), false);
                    }
                    else if ((stream.SinceSamples += chunk.Pcm.Length) >= stream.Rate * PartialEvery)
                    {
                        stream.SinceSamples = 0;
                        Emit(chunk.Player, Text(PartialField, stream.Recognizer.PartialResult()), true);
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Алиса: ошибка распознавания: " + e.Message);
                }
            }
            foreach (var stream in streams.Values)
                stream.Recognizer.Dispose();
            model.Dispose();
        }

        private static string Text(Regex field, string json)
        {
            var match = field.Match(json ?? "");
            return match.Success ? match.Groups[1].Value : "";
        }

        private void Emit(int player, string text, bool partial)
        {
            if (text != null && text.Trim().Length == 0)
                return;
            lock (gate)
                results.Enqueue(new Heard { Player = player, Text = text, Partial = partial });
        }

        private static string GrammarJson()
        {
            string[] custom;
            lock (YandexStationItem.CustomTopics)
                custom = YandexStationItem.CustomTopics.ToArray();
            var phrases = new List<string>();
            var all = Grammar.Concat(custom).Select(p => p.Trim().ToLowerInvariant().Replace("\"", "")).Where(p => p.Length > 0).ToList();
            foreach (var t in all.Concat(all.SelectMany(p => p.Split(' '))).Where(w => w.Length > 0).Distinct())
                phrases.Add("\"" + t + "\"");
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
