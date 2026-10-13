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
    ///
    /// Vosk быстрый, но знает мало слов. Поэтому фразу, обращённую к колонке (есть «Алиса» или
    /// колонка ждёт ответа), ещё раз распознаёт Whisper (<see cref="AliceWhisper"/>) по её звуку
    /// целиком, и колонка получает его текст. Не успел за <see cref="AliceWhisper.TimeoutSeconds"/>,
    /// не загрузился или написал мусор — текст Vosk.
    ///
    /// «Алиса» ловит отдельная маленькая модель (vosk/model-wake, vosk-model-small-ru-0.22): у большой
    /// акустики слова появляются в промежуточном результате на 1,5–2 с позже, чем сказаны, а маленькая
    /// отдаёт «алиса» через ~0,2 с после конца слова. Нет маленькой — просыпается по большой, как раньше.
    ///
    /// Простой детектор речи по громкости (<see cref="Vad"/>) отличает слова от тишины и шума: после «Алиса»
    /// вопросом считается только то, где наберётся хотя бы <see cref="MinSpeech"/> с речи, конец вопроса —
    /// пауза <see cref="EndPause"/> с, а колонка ждёт, пока звучит речь, а не пока Vosk пишет слова из шума.
    ///
    /// Несколько игроков: голос каждого приходит отдельно (свой микрофон хоста и голос каждого игрока из
    /// чата), у каждого свой распознаватель. Проснувшись, колонка слушает того, кто сказал «Алиса»
    /// (<see cref="YandexStationItem.IsHearing"/>): фразы остальных без «Алиса» в это время не вопрос.
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

        /// <summary>Уточнять фразы для колонки через Whisper (если он лежит рядом с модом).</summary>
        public static bool UseWhisper = true;

        /// <summary>Сколько секунд речи (по громкости) нужно после «Алиса», чтобы это был вопрос, а не шум.</summary>
        public static float MinSpeech = 0.3f;
        /// <summary>Пауза (секунды), которой кончается вопрос после «Алиса» или ответ на вопрос колонки.</summary>
        public static float EndPause = 0.8f;

        // Междометия и обрывки, которые Vosk пишет на шум и дыхание: одни они — не вопрос
        private static readonly HashSet<string> Fillers = new HashSet<string>
        {
            "а", "и", "э", "ээ", "м", "мм", "ммм", "ну", "о", "у", "ох", "ах", "эх", "хм", "аа", "ы", "ой", "ага", "угу",
        };

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
        // Чьих слов колонка сейчас ждёт без «Алиса» (проснулась на него, спросила): им вопрос кончается паузой
        private readonly HashSet<int> hearing = new HashSet<int>();
        private Thread worker;

        // Кто сейчас слушается: игрок → до какого времени (говорил + хвост)
        private readonly Dictionary<int, float> hearUntil = new Dictionary<int, float>();
        private readonly Dictionary<int, string> lastPartial = new Dictionary<int, string>();
        private readonly HashSet<int> answered = new HashSet<int>();
        private readonly HashSet<VoiceTap> taps = new HashSet<VoiceTap>(), touchedTaps = new HashSet<VoiceTap>();
        // Фразы, которые ждут Whisper (и всё, что тот же игрок сказал после них, — по порядку)
        private readonly List<Pending> pending = new List<Pending>();
        private volatile bool micOn;
        private bool micWasOn;
        private readonly PreRoll micPre = new PreRoll();
        private volatile int micPlayer;
        private int outputRate;

        internal struct Chunk
        {
            public int Player, Rate;
            public short[] Pcm; // null — игрок замолчал, дожать фразу
        }

        internal enum HeardKind
        {
            Text,
            Reset, // распознаватель сброшен после ответа на кусок фразы
            Wake,  // маленькая модель услышала «Алиса»
            Voice, // слышна речь (по громкости): человек ещё говорит
        }

        internal struct Heard
        {
            public int Player;
            public HeardKind Kind;
            public string Text;
            public bool Partial;
            // Секунд речи во фразе и после «Алиса» (-1 — «Алиса» в этой фразе маленькая модель не слышала)
            public float Speech, SpeechAfterWake;
            // Звук этой фразы с начала (с прошлого сброса или конца прошлой фразы): первые AudioCount
            // отсчётов, частота Rate. Массив дальше не меняется, его можно читать с любого потока
            public short[] Audio;
            public int AudioCount, Rate;
        }

        private class Pending
        {
            public int Player;
            public string Vosk;
            public bool Wake;
            public AliceWhisper.Job Job;
            public float GiveUpAt;
        }

        private void Awake() =>
            YandexStationItem.ResetHearing = ResetPlayer;

        // Настройки Whisper. Plugin создаёт этот объект в своём Awake, когда Chainloader ещё не знает
        // экземпляр плагина, поэтому конфиг читаем здесь
        private void Start()
        {
            var plugin = BepInEx.Bootstrap.Chainloader.PluginInfos.Values.Select(p => p.Instance).OfType<Plugin>().FirstOrDefault();
            if (plugin == null)
                return;
            const string section = "Yandex Station";
            UseWhisper = plugin.Config.Bind(section, "WhisperRecognition", true,
                "Фразы для Алисы («Алиса, …» и ответы на её вопросы) распознаёт ещё и Whisper (папка whisper/ рядом с модом): " +
                "понимает любую речь, а не только слова из intents.txt. Модель грузится, когда впервые включают колонку").Value;
            AliceWhisper.UseGpu = plugin.Config.Bind(section, "WhisperGpu", true,
                "Whisper на видеокарте (Vulkan, любой производитель), иначе на процессоре (медленнее). " +
                "Выключите, если игра падает или тормозит после включения колонки").Value;
            AliceWhisper.TimeoutSeconds = Mathf.Clamp(plugin.Config.Bind(section, "WhisperTimeout", 3f,
                "Сколько секунд ждать Whisper; не успел — колонка отвечает по тексту Vosk").Value, 0.5f, 10f);
            MinSpeech = Mathf.Clamp(plugin.Config.Bind(section, "MinSpeech", 0.3f,
                "Сколько секунд речи нужно после «Алиса», чтобы колонка сочла это вопросом. Тишина, шум и «э-э» короче — " +
                "не вопрос: колонка ждёт дальше, а через ListenSilence секунд выключается").Value, 0.1f, 2f);
            EndPause = Mathf.Clamp(plugin.Config.Bind(section, "EndPause", 0.8f,
                "Пауза (секунды), после которой вопрос к колонке считается законченным").Value, 0.3f, 3f);
        }

        // Модель Whisper большая (~264 МБ): грузим в фоне, когда впервые включили колонку
        private void StartWhisper()
        {
            AliceWhisper.Dir = Path.Combine(Path.GetDirectoryName(VoskDir) ?? "", "whisper");
            AliceWhisper.LogInfo = Plugin.Log.LogInfo;
            AliceWhisper.LogWarning = Plugin.Log.LogWarning;
            if (!File.Exists(Path.Combine(AliceWhisper.Dir, AliceWhisper.ModelFile)))
            {
                UseWhisper = false;
                Plugin.Log.LogWarning($"Алиса: нет {AliceWhisper.ModelFile} в {AliceWhisper.Dir}, распознаю только Vosk");
                return;
            }
            var intents = "";
            try
            {
                using (var stream = typeof(AliceListener).Assembly.GetManifestResourceStream("SidiuszCustomItems.intents.txt"))
                {
                    if (stream != null)
                        intents = new StreamReader(stream).ReadToEnd();
                }
                var own = Path.Combine(BepInEx.Paths.ConfigPath, "SidiuszCustomItems.intents.txt");
                if (File.Exists(own))
                    intents += "\n" + File.ReadAllText(own);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Алиса: подсказка для Whisper без intents.txt: " + e.Message);
            }
            AliceWhisper.BuildPrompt(YandexStationItem.SpokenNames.Values.SelectMany(n => n), intents);
            AliceWhisper.LoadAsync();
        }

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
            lock (gate)
                hearing.Clear();
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
                    if (on && YandexStationItem.IsHearing(p))
                    {
                        lock (gate)
                            hearing.Add(i);
                    }

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
            if (listen && UseWhisper && !AliceWhisper.Started && VoskDir != null && YandexStationItem.AnyOn)
                StartWhisper();

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
            FlushPending(round);
        }

        private void Handle(Heard heard, StartOfRound round)
        {
            if (heard.Kind == HeardKind.Reset)
            {
                answered.Remove(heard.Player); // распознаватель сброшен, дальше новая фраза
                return;
            }
            var player = round != null && heard.Player >= 0 && heard.Player < round.allPlayerScripts.Length
                ? round.allPlayerScripts[heard.Player] : null;
            if (player == null)
                return;

            switch (heard.Kind)
            {
                case HeardKind.Voice:
                    // Слышна речь — человек ещё говорит: колонка подождёт с выключением (шум и тишина не продлевают)
                    YandexStationItem.OnVoice(player);
                    return;
                case HeardKind.Wake:
                    Plugin.Log.LogInfo($"Алиса: {player.playerUsername} позвал(а) «{YandexStationItem.WakeWord}»");
                    YandexStationItem.OnPartial(player, YandexStationItem.WakeWord);
                    return;
            }

            if (heard.Partial)
            {
                // «Алиса» уже прозвучала — колонка просыпается, не дожидаясь конца фразы
                if (!answered.Contains(heard.Player))
                    YandexStationItem.OnPartial(player, heard.Text);
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
            Deliver(heard, player);
        }

        /// <summary>
        /// Фраза Vosk → колонке. Обращённую к Алисе сначала уточняет Whisper (по звуку фразы), остальное сразу.
        /// Ответ по куску фразы (IsCompleteCommand) — тоже через Whisper, по звуку, сказанному до сих пор.
        /// </summary>
        private void Deliver(Heard heard, GameNetcodeStuff.PlayerControllerB player)
        {
            var wake = heard.Text.Contains(YandexStationItem.WakeWord);
            if (!Speech(ref heard, player, wake))
                return;
            AliceWhisper.Job job = null;
            // Одно «Алиса» Whisper не уточнит: колонка и так проснётся. Без речи после «Алиса» (тишина, шум)
            // он бы дописал что-нибудь своё, поэтому в Whisper — только фразы с вопросом
            var onlyWake = heard.Text.Trim() == YandexStationItem.WakeWord;
            if (UseWhisper && AliceWhisper.Ready && heard.Audio != null && !onlyWake
                && (wake || YandexStationItem.IsHearing(player)))
                job = AliceWhisper.Start(heard.Audio, heard.AudioCount, heard.Rate);
            else if (UseWhisper && !AliceWhisper.Ready && !onlyWake && (wake || YandexStationItem.IsHearing(player)))
                Plugin.Log.LogInfo($"Алиса: Whisper {(AliceWhisper.Started ? "ещё грузится" : "не запущен")}, отвечаю по Vosk «{heard.Text}»");
            if (job == null && !pending.Any(p => p.Player == heard.Player))
            {
                Speak(player, heard.Text);
                return;
            }
            pending.Add(new Pending
            {
                Player = heard.Player,
                Vosk = heard.Text,
                Wake = wake,
                Job = job,
                GiveUpAt = Time.realtimeSinceStartup + AliceWhisper.TimeoutSeconds,
            });
        }

        /// <summary>
        /// Есть ли во фразе речь, а не шум. Шум после «Алиса» отрезается (колонка слышит одно «Алиса» и ждёт дальше),
        /// фраза без «Алиса» из одного шума выбрасывается.
        /// </summary>
        private static bool Speech(ref Heard heard, GameNetcodeStuff.PlayerControllerB player, bool wake)
        {
            var words = heard.Text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var wakeAt = Array.IndexOf(words, YandexStationItem.WakeWord);
            var rest = words.Skip(wakeAt + 1).Where(w => w != "[unk]" && !Fillers.Contains(w)).ToArray();
            var after = heard.SpeechAfterWake >= 0f ? heard.SpeechAfterWake : heard.Speech;
            if (wake)
            {
                if (rest.Length == 0)
                    heard.Text = YandexStationItem.WakeWord;
                else if (after < MinSpeech && heard.Audio != null)
                {
                    Plugin.Log.LogInfo($"Алиса: после «{YandexStationItem.WakeWord}» у {player.playerUsername} только шум " +
                                        $"({after:0.00} с речи, Vosk «{heard.Text}»), жду вопрос");
                    heard.Text = YandexStationItem.WakeWord;
                }
                return true;
            }
            if (rest.Length == 0 || heard.Audio != null && heard.Speech < MinSpeech)
            {
                if (YandexStationItem.IsHearing(player))
                    Plugin.Log.LogInfo($"Алиса: {player.playerUsername}: «{heard.Text}» — шум, а не речь ({heard.Speech:0.00} с), пропускаю");
                return false;
            }
            return true;
        }

        // Готовые фразы из Whisper → колонке, по порядку для каждого игрока. Не успел — текст Vosk
        private void FlushPending(StartOfRound round)
        {
            for (var i = 0; i < pending.Count; i++)
            {
                var p = pending[i];
                var earlier = false;
                for (var j = 0; j < i && !earlier; j++)
                    earlier = pending[j].Player == p.Player;
                if (earlier)
                    continue;
                var player = round != null && round.allPlayerScripts != null && p.Player >= 0 && p.Player < round.allPlayerScripts.Length
                    ? round.allPlayerScripts[p.Player] : null;
                if (p.Job != null && !p.Job.Done && Time.realtimeSinceStartup < p.GiveUpAt)
                {
                    // Пока Whisper думает, колонка не должна перестать слушать
                    if (player != null)
                        YandexStationItem.OnVoice(player);
                    continue;
                }
                pending.RemoveAt(i--);
                var text = p.Vosk;
                if (p.Job != null)
                {
                    if (!p.Job.Done)
                    {
                        p.Job.Cancelled = true;
                        Plugin.Log.LogInfo($"Алиса: Whisper не успел за {AliceWhisper.TimeoutSeconds:0.0} с, беру Vosk «{p.Vosk}»");
                    }
                    else if (p.Job.Text == null)
                    {
                        Plugin.Log.LogInfo($"Алиса: Whisper «{p.Job.Raw}» не годится ({p.Job.Why}), беру Vosk «{p.Vosk}»");
                    }
                    else
                    {
                        text = AliceWhisper.WithWake(p.Job.Text, p.Wake, YandexStationItem.WakeWord);
                        Plugin.Log.LogInfo($"Алиса: Vosk «{p.Vosk}» → Whisper «{text}» ({p.Job.Seconds:0.00} с)");
                    }
                }
                if (player != null)
                    Speak(player, text);
            }
        }

        private static void Speak(GameNetcodeStuff.PlayerControllerB player, string text)
        {
            Plugin.Log.LogInfo($"Алиса услышала {player.playerUsername}: {text}");
            YandexStationItem.OnSpeech(player, text);
        }

        private void OnDestroy()
        {
            stopping = true;
            lock (gate)
                Monitor.PulseAll(gate);
        }

        // Вызывает Dissonance (может быть не с главного потока): только копируем звук в очередь
        // Пока игрок молчит, звук не распознаётся, но последние доли секунды хранятся: чат замечает речь
        // с опозданием, и без них начало «Алиса» терялось бы
        public void ReceiveMicrophoneData(ArraySegment<float> buffer, WaveFormat format)
        {
            if (buffer.Array == null)
                return;
            var pcm = new short[buffer.Count];
            for (var i = 0; i < buffer.Count; i++)
                pcm[i] = ToShort(buffer.Array[buffer.Offset + i]);
            if (!micOn)
            {
                micWasOn = false;
                micPre.Keep(pcm, format.SampleRate);
                return;
            }
            if (!micWasOn)
            {
                micWasOn = true;
                micPre.Flush(this, micPlayer, format.SampleRate);
            }
            Push(new Chunk { Player = micPlayer, Rate = format.SampleRate, Pcm = pcm });
        }

        /// <summary>Последние <see cref="Seconds"/> секунды звука до того, как игрок «заговорил» в чате.</summary>
        internal class PreRoll
        {
            private const float Seconds = 0.4f;
            private readonly Queue<short[]> parts = new Queue<short[]>();
            private int count, rate;

            public void Keep(short[] pcm, int sampleRate)
            {
                if (sampleRate != rate)
                {
                    parts.Clear();
                    count = 0;
                    rate = sampleRate;
                }
                parts.Enqueue(pcm);
                count += pcm.Length;
                while (parts.Count > 1 && count - parts.Peek().Length >= rate * Seconds)
                    count -= parts.Dequeue().Length;
            }

            public void Flush(AliceListener listener, int player, int sampleRate)
            {
                if (sampleRate == rate)
                {
                    foreach (var pcm in parts)
                        listener.Push(new Chunk { Player = player, Rate = rate, Pcm = pcm });
                }
                parts.Clear();
                count = 0;
            }
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
            private bool wasOn;
            private readonly PreRoll pre = new PreRoll();

            private void OnAudioFilterRead(float[] data, int channels)
            {
                var listener = Listener;
                if (listener == null || Rate <= 0 || channels <= 0)
                    return;
                var pcm = new short[data.Length / channels];
                for (var i = 0; i < pcm.Length; i++)
                    pcm[i] = ToShort(data[i * channels]);
                if (!On)
                {
                    wasOn = false;
                    pre.Keep(pcm, Rate);
                    return;
                }
                if (!wasOn)
                {
                    wasOn = true;
                    pre.Flush(listener, Player, Rate);
                }
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
            // Маленькая модель только для «Алиса» (null — её нет)
            public VoskRecognizer Wake;
            public int Rate;
            public int SinceSamples, WakeSinceSamples;
            // Звук текущей фразы для Whisper. Новая фраза — новый массив: старый остаётся тому, кто его взял
            public short[] Audio;
            public int AudioCount;
            public readonly Vad Vad = new Vad();
            // Речь во фразе, отсчётов: всего и после «Алиса»; где во фразе прозвучало «Алиса» (-1 — не звучало)
            public int Speech, SpeechAfterWake, WakeAt = -1;
            // Отсчётов тишины после последней речи и с прошлого сигнала «говорит»
            public int Silence, SinceVoice = int.MaxValue / 2;

            public void Append(short[] pcm)
            {
                var max = Rate * MaxPhraseSeconds;
                var need = AudioCount + pcm.Length;
                if (need > max)
                    return; // длиннее Whisper всё равно не возьмёт, да и это уже не вопрос, а болтовня
                if (Audio == null || need > Audio.Length)
                {
                    var bigger = new short[Math.Min(max, Math.Max(need, Audio == null ? Rate * 4 : Audio.Length * 2))];
                    if (Audio != null)
                        Array.Copy(Audio, bigger, AudioCount);
                    Audio = bigger;
                }
                Array.Copy(pcm, 0, Audio, AudioCount, pcm.Length);
                AudioCount = need;
            }

            public void NewPhrase()
            {
                Audio = null;
                AudioCount = 0;
                Speech = SpeechAfterWake = 0;
                WakeAt = -1;
            }
        }

        /// <summary>
        /// Речь или нет — по громкости кадров по 20 мс: речь, если кадр заметно громче фона (шум микрофона, гул)
        /// и не совсем тихий. Фон следит за самыми тихими кадрами: вниз сразу, вверх медленно.
        /// </summary>
        internal class Vad
        {
            private const float Above = 3.2f;   // ≈ +10 дБ над фоном
            private const float MinLevel = 200f; // ≈ −44 дБ: тише — не речь, как бы ни было тихо вокруг
            private float floor = -1f;
            private double sum;
            private int filled;

            /// <summary>Сколько отсчётов уже услышанного куска звука — речь (по нынешнему фону, не меняя его).</summary>
            public int Count(short[] audio, int from, int to, int rate)
            {
                var frame = Math.Max(1, rate / 50);
                var voiced = 0;
                for (var start = from; start + frame <= to; start += frame)
                {
                    double sq = 0;
                    for (var i = start; i < start + frame; i++)
                        sq += audio[i] * (double)audio[i];
                    var rms = (float)Math.Sqrt(sq / frame);
                    if (rms > MinLevel && rms > Math.Max(floor, 0f) * Above)
                        voiced += frame;
                }
                return voiced;
            }

            /// <summary>Сколько отсчётов <paramref name="pcm"/> пришлось на речь.</summary>
            public int Voiced(short[] pcm, int rate)
            {
                var frame = Math.Max(1, rate / 50);
                var voiced = 0;
                foreach (var v in pcm)
                {
                    sum += v * (double)v;
                    if (++filled < frame)
                        continue;
                    var rms = (float)Math.Sqrt(sum / filled);
                    floor = floor < 0f || rms < floor ? Math.Max(rms, 30f) : floor + (rms - floor) * 0.002f;
                    if (rms > MinLevel && rms > floor * Above)
                        voiced += filled;
                    sum = 0;
                    filled = 0;
                }
                return voiced;
            }
        }

        private const int MaxPhraseSeconds = 25;

        private void Work()
        {
            Model model, wakeModel = null;
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
            try
            {
                var wakeDir = Path.Combine(VoskDir, "model-wake");
                if (Directory.Exists(wakeDir))
                    wakeModel = new Model(wakeDir);
                else
                    Plugin.Log.LogWarning($"Алиса: нет {wakeDir}, «{YandexStationItem.WakeWord}» слышу по большой модели (с задержкой)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Алиса: не загрузилась модель для «Алиса», слышу его по большой модели: " + e.Message);
            }

            var grammar = Grammar != null && Grammar.Length > 0 ? GrammarJson() : null;
            var streams = new Dictionary<int, Stream>();
            while (!stopping)
            {
                Chunk chunk;
                bool reset, listening;
                lock (gate)
                {
                    while (chunks.Count == 0 && !stopping)
                        Monitor.Wait(gate, 500);
                    if (stopping)
                        break;
                    chunk = chunks.Dequeue();
                    reset = resetRequests.Remove(chunk.Player);
                    listening = hearing.Contains(chunk.Player);
                }

                try
                {
                    streams.TryGetValue(chunk.Player, out var stream);
                    if (reset && stream != null)
                    {
                        stream.Recognizer.Reset();
                        stream.NewPhrase();
                        Emit(new Heard { Player = chunk.Player, Kind = HeardKind.Reset });
                    }
                    if (chunk.Pcm == null)
                    {
                        // Игрок замолчал: дожимаем фразу
                        if (stream != null && !reset)
                            Finish(chunk.Player, stream);
                        stream?.Wake?.Reset();
                        continue;
                    }
                    if (stream == null || stream.Rate != chunk.Rate)
                    {
                        stream?.Recognizer.Dispose();
                        stream?.Wake?.Dispose();
                        stream = new Stream
                        {
                            Rate = chunk.Rate,
                            Recognizer = grammar != null
                                ? new VoskRecognizer(model, chunk.Rate, grammar)
                                : new VoskRecognizer(model, chunk.Rate),
                            Wake = wakeModel != null ? new VoskRecognizer(wakeModel, chunk.Rate) : null,
                        };
                        streams[chunk.Player] = stream;
                    }

                    stream.Append(chunk.Pcm);
                    var voiced = stream.Vad.Voiced(chunk.Pcm, stream.Rate);
                    stream.Speech += voiced;
                    if (stream.WakeAt >= 0)
                        stream.SpeechAfterWake += voiced;
                    stream.Silence = voiced > 0 ? 0 : stream.Silence + chunk.Pcm.Length;
                    stream.SinceVoice += chunk.Pcm.Length;
                    if (voiced > 0 && stream.SinceVoice >= stream.Rate / 4)
                    {
                        stream.SinceVoice = 0;
                        Emit(new Heard { Player = chunk.Player, Kind = HeardKind.Voice });
                    }

                    if (stream.Wake != null)
                        ListenWake(chunk, stream);

                    if (stream.Recognizer.AcceptWaveform(chunk.Pcm, chunk.Pcm.Length))
                    {
                        stream.SinceSamples = 0;
                        Emit(chunk.Player, Text(TextField, stream.Recognizer.Result()), false, stream);
                        stream.NewPhrase();
                    }
                    else if ((stream.WakeAt >= 0 || listening) && stream.Speech >= stream.Rate * MinSpeech
                             && stream.Silence >= stream.Rate * EndPause)
                    {
                        // Вопрос к колонке кончился паузой: не ждём, пока большая модель сама дойдёт до конца фразы
                        // (она отстаёт на 1,5–2 с), а дожимаем её сейчас
                        stream.SinceSamples = 0;
                        Finish(chunk.Player, stream);
                    }
                    else if ((stream.SinceSamples += chunk.Pcm.Length) >= stream.Rate * PartialEvery)
                    {
                        stream.SinceSamples = 0;
                        Emit(chunk.Player, Text(PartialField, stream.Recognizer.PartialResult()), true, stream);
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Алиса: ошибка распознавания: " + e.Message);
                }
            }
            foreach (var stream in streams.Values)
            {
                stream.Recognizer.Dispose();
                stream.Wake?.Dispose();
            }
            model.Dispose();
            wakeModel?.Dispose();
        }

        private void Finish(int player, Stream stream)
        {
            Emit(player, Text(TextField, stream.Recognizer.FinalResult()), false, stream);
            stream.NewPhrase();
        }

        // «Алиса» по маленькой модели: как только слово есть в промежуточном результате — будим колонку
        private void ListenWake(Chunk chunk, Stream stream)
        {
            string text;
            if (stream.Wake.AcceptWaveform(chunk.Pcm, chunk.Pcm.Length))
                text = Text(TextField, stream.Wake.Result());
            else if ((stream.WakeSinceSamples += chunk.Pcm.Length) >= stream.Rate * WakeEvery)
                text = Text(PartialField, stream.Wake.PartialResult());
            else
                return;
            stream.WakeSinceSamples = 0;
            if (Array.IndexOf(text.Split(' '), YandexStationItem.WakeWord) < 0)
                return;
            stream.Wake.Reset(); // одно «Алиса» — один раз
            // Модель отдаёт слово чуть позже его конца: речь после «Алиса» считаем с запасом назад
            if (stream.WakeAt < 0)
            {
                stream.WakeAt = Math.Max(0, stream.AudioCount - (int)(stream.Rate * WakeLag));
                stream.SpeechAfterWake = stream.Vad.Count(stream.Audio, stream.WakeAt, stream.AudioCount, stream.Rate);
            }
            Emit(new Heard { Player = chunk.Player, Kind = HeardKind.Wake });
        }

        // Как часто спрашивать маленькую модель про «Алиса» и на сколько она отстаёт от конца слова
        private const float WakeEvery = 0.1f, WakeLag = 0.2f;

        private static string Text(Regex field, string json)
        {
            var match = field.Match(json ?? "");
            return match.Success ? match.Groups[1].Value : "";
        }

        private void Emit(int player, string text, bool partial, Stream stream)
        {
            if (text.Trim().Length == 0)
                return;
            var heard = new Heard { Player = player, Text = text, Partial = partial };
            if (stream.AudioCount > 0)
            {
                heard.Audio = stream.Audio;
                heard.AudioCount = stream.AudioCount;
                heard.Rate = stream.Rate;
                heard.Speech = stream.Speech / (float)stream.Rate;
                heard.SpeechAfterWake = stream.WakeAt >= 0 ? stream.SpeechAfterWake / (float)stream.Rate : -1f;
            }
            else
            {
                heard.SpeechAfterWake = -1f;
            }
            Emit(heard);
        }

        private void Emit(Heard heard)
        {
            lock (gate)
                results.Enqueue(heard);
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
