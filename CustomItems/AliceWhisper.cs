using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CustomItems
{
    /// <summary>
    /// Точное распознавание того, что сказали Алисе: whisper.cpp (модель ggml-small) поверх Vosk.
    /// Vosk слушает всё подряд и сразу ловит «Алиса» (он быстрый, но знает мало слов). Когда фраза
    /// обращена к колонке, её звук целиком уходит сюда, и Whisper пишет её обычным текстом:
    /// «алиса а ты вообще знаешь что такое любовь», а не «алиса [unk] знаешь [unk]».
    ///
    /// Своя тонкая обёртка над C API whisper.cpp (без Whisper.net: тому нужны System.Memory,
    /// Microsoft.Extensions.AI и т.д., в игре это лишние сборки и конфликты с другими модами).
    /// Только то, что есть в Mono игры: DllImport, структуры по значению, Marshal.
    ///
    /// Лежит рядом с DLL мода, папка whisper/: модель ggml-small-q8_0.bin (264 МБ, её качает сборка) и
    /// нативные библиотеки Windows x64 в двух вариантах: vulkan/ (видеокарта любого производителя)
    /// и cpu/. Без Vulkan (или WhisperGpu = false) — процессор, не больше min(4, ядер/2) потоков.
    ///
    /// Без Unity: этот же файл собирается в тест на Linux (Tools/YandexStation/whisper).
    /// </summary>
    public static class AliceWhisper
    {
        /// <summary>Папка whisper/ рядом с DLL мода.</summary>
        public static string Dir;
        public static string ModelFile = "ggml-small-q8_0.bin";
        /// <summary>Пробовать видеокарту (Vulkan). false — только процессор.</summary>
        public static bool UseGpu = true;
        /// <summary>Потоков процессора на распознавание: игре остаётся хотя бы половина ядер.</summary>
        public static int Threads = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));
        /// <summary>
        /// Подсказка модели: слова, которые она должна писать именно так (имена друзей, луны, предметы).
        /// Собирается в <see cref="BuildPrompt"/>; менять до загрузки.
        /// </summary>
        public static string Prompt = "";
        /// <summary>Если дольше — бросаем (колонка уже ответила по Vosk).</summary>
        public static float TimeoutSeconds = 3f;
        /// <summary>
        /// На процессоре окно кодировщика по длине фразы, а не все 30 секунд: втрое быстрее
        /// (3.3 с против 11 с на тесте), точность чуть ниже.
        /// </summary>
        public static bool ShortWindow = true;
        /// <summary>Меньше этого окна (кадров по 20 мс, из 1500) не берём: на коротких фразах начинает путаться.</summary>
        public static int MinWindow = 384;

        public static Action<string> LogInfo, LogWarning;

        private const int Rate = 16000;

        private static int state; // 0 — не грузили, 1 — грузится, 2 — готов, 3 — не вышло
        public static bool Started => state != 0;
        public static bool Ready => state == 2;
        /// <summary>Чем считает: «Vulkan», «CPU» (для лога).</summary>
        public static string Backend { get; private set; }

        private static IntPtr ctx, language, prompt;
        private static string promptText;
        private static readonly object gate = new object();
        private static readonly Queue<Job> jobs = new Queue<Job>();
        private static volatile Job current;
        private static readonly AbortCallback abort = Abort; // держим ссылку: колбэк зовётся из whisper.cpp

        /// <summary>Одна фраза на распознавание. Готово, когда <see cref="Done"/>.</summary>
        public sealed class Job
        {
            internal float[] Samples;
            internal long AbortAt;
            /// <summary>Текст (нормализованный, см. <see cref="Clean"/>) или null — не вышло или мусор.</summary>
            public string Text;
            /// <summary>Как написал Whisper, до чистки (для лога).</summary>
            public string Raw;
            /// <summary>Почему нет текста (для лога).</summary>
            public string Why;
            public double Seconds;
            public volatile bool Done;
            /// <summary>Уже не нужно (колонка ответила по Vosk): не начинать или оборвать.</summary>
            public volatile bool Cancelled;
        }

        /// <summary>Загрузить модель в фоне (один раз). Пока грузится, колонка слушает только Vosk.</summary>
        public static void LoadAsync()
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
                return;
            new Thread(Work) { IsBackground = true, Name = "SidiuszCustomItems.Whisper", Priority = ThreadPriority.BelowNormal }.Start();
        }

        /// <summary>
        /// Поставить фразу в очередь: <paramref name="pcm"/> — первые <paramref name="count"/>
        /// отсчётов, моно, <paramref name="rate"/> Гц. null — Whisper не готов.
        /// </summary>
        public static Job Start(short[] pcm, int count, int rate)
        {
            if (!Ready || pcm == null || count <= 0 || rate <= 0)
                return null;
            var job = new Job { Samples = Resample(pcm, count, rate) };
            lock (gate)
            {
                jobs.Enqueue(job);
                Monitor.Pulse(gate);
            }
            return job;
        }

        private static void Work()
        {
            try
            {
                Load();
                state = 2;
            }
            catch (Exception e)
            {
                state = 3;
                LogWarning?.Invoke("Алиса: Whisper не загрузился, распознаю только Vosk. " + e.Message);
                return;
            }
            while (true)
            {
                Job job;
                lock (gate)
                {
                    while (jobs.Count == 0)
                        Monitor.Wait(gate);
                    job = jobs.Dequeue();
                }
                if (job.Cancelled)
                {
                    job.Why = "не дождались";
                    job.Done = true;
                    continue;
                }
                try
                {
                    Run(job);
                }
                catch (Exception e)
                {
                    job.Text = null;
                    job.Why = "ошибка: " + e.Message;
                }
                job.Done = true;
            }
        }

        /// <summary>Загрузить библиотеки и модель (с потока, который потом и распознаёт).</summary>
        public static void Load()
        {
            var model = Path.Combine(Dir ?? "", ModelFile);
            if (!File.Exists(model))
                throw new FileNotFoundException("нет модели " + model);
            Backend = LoadNatives();

            var cp = whisper_context_default_params_by_ref();
            var cparams = (ContextParams)Marshal.PtrToStructure(cp, typeof(ContextParams));
            whisper_free_context_params(cp);
            cparams.UseGpu = (byte)(UseGpu ? 1 : 0);
            cparams.FlashAttn = 1;

            // Путь к модели в Windows может быть с кириллицей (папка пользователя), а whisper.cpp открывает
            // файл узкой строкой: читаем сами и отдаём буфер
            var watch = Stopwatch.StartNew();
            var size = new FileInfo(model).Length;
            var buffer = Marshal.AllocHGlobal(new IntPtr(size));
            try
            {
                using (var file = new FileStream(model, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                {
                    var chunk = new byte[1 << 20];
                    long done = 0;
                    int n;
                    while ((n = file.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        Marshal.Copy(chunk, 0, new IntPtr(buffer.ToInt64() + done), n);
                        done += n;
                    }
                }
                ctx = whisper_init_from_buffer_with_params(buffer, new UIntPtr((ulong)size), cparams);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            if (ctx == IntPtr.Zero)
                throw new Exception("whisper_init_from_buffer_with_params вернула null");
            language = Utf8("ru");

            // Прогрев: первый проход заметно дольше (выделение буферов, шейдеры Vulkan)
            var warm = new Job { Samples = new float[Rate], AbortAt = long.MaxValue };
            Run(warm);
            LogInfo?.Invoke($"Алиса: Whisper загружен ({Backend}, {ModelFile}, потоков {Threads}) за {watch.Elapsed.TotalSeconds:0.0} с");
        }

        /// <summary>Распознать фразу (с потока загрузки). Результат в полях <paramref name="job"/>.</summary>
        public static void Run(Job job)
        {
            var watch = Stopwatch.StartNew();
            var samples = job.Samples;
            // Короче секунды whisper.cpp не берёт: добиваем тишиной
            if (samples.Length < Rate * 3 / 2)
                Array.Resize(ref samples, Rate * 3 / 2);
            if (job.AbortAt == 0)
                job.AbortAt = Stopwatch.GetTimestamp() + (long)(TimeoutSeconds * Stopwatch.Frequency);

            if (!ReferenceEquals(promptText, Prompt))
            {
                if (prompt != IntPtr.Zero)
                    Marshal.FreeHGlobal(prompt);
                promptText = Prompt;
                prompt = string.IsNullOrEmpty(Prompt) ? IntPtr.Zero : Utf8(Prompt);
            }

            var pp = whisper_full_default_params_by_ref(0); // жадный поиск
            var p = (FullParams)Marshal.PtrToStructure(pp, typeof(FullParams));
            whisper_free_params(pp);
            p.NThreads = Threads;
            p.Language = language;
            p.DetectLanguage = 0;
            p.Translate = 0;
            p.NoContext = 1;
            p.NoTimestamps = 1;
            p.SingleSegment = 1;
            p.PrintProgress = p.PrintRealtime = p.PrintSpecial = p.PrintTimestamps = 0;
            p.InitialPrompt = prompt;
            p.SuppressNst = 1;
            p.Temperature = 0f;
            p.TemperatureInc = 0f; // без повторов с температурой: дольше, а для коротких фраз не лучше
            p.MaxTokens = 64;
            // Окно кодировщика: 50 кадров на секунду звука (+запас). Только на процессоре: видеокарта и полное
            // окно считает быстро, а с ним точнее
            if (ShortWindow && Backend != "Vulkan")
                p.AudioCtx = Math.Min(1500, Math.Max(MinWindow, samples.Length * 50 / Rate + 64));
            p.AbortCallback = Marshal.GetFunctionPointerForDelegate(abort);
            p.AbortCallbackUserData = IntPtr.Zero;

            current = job;
            int result;
            try
            {
                result = whisper_full(ctx, p, samples, samples.Length);
            }
            finally
            {
                current = null;
            }
            job.Seconds = watch.Elapsed.TotalSeconds;
            if (result != 0)
            {
                job.Why = job.Cancelled || Stopwatch.GetTimestamp() > job.AbortAt ? "оборвали по времени" : "whisper_full вернула " + result;
                return;
            }
            var text = new StringBuilder();
            var n = whisper_full_n_segments(ctx);
            for (var i = 0; i < n; i++)
                text.Append(FromUtf8(whisper_full_get_segment_text(ctx, i))).Append(' ');
            job.Raw = text.ToString().Trim();
            var seconds = job.Samples.Length / (double)Rate;
            job.Why = Junk(job.Raw, seconds);
            job.Text = job.Why == null ? Clean(job.Raw) : null;
        }

        private static bool Abort(IntPtr data)
        {
            var job = current;
            return job != null && (job.Cancelled || Stopwatch.GetTimestamp() > job.AbortAt);
        }

        // ---- Текст ----

        /// <summary>Как у Vosk: строчные, «ё» → «е», без знаков (дефис внутри слова остаётся: «кто-то»).</summary>
        public static string Clean(string text)
        {
            text = (text ?? "").ToLowerInvariant().Replace('ё', 'е');
            var sb = new StringBuilder(text.Length);
            var depth = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                // Звуки в скобках («[смех]», «(музыка)») — не слова
                if (c == '[' || c == '(')
                    depth++;
                else if ((c == ']' || c == ')') && depth > 0)
                    depth--;
                if (depth > 0 || c == ']' || c == ')')
                {
                    sb.Append(' ');
                    continue;
                }
                var keep = char.IsLetterOrDigit(c)
                           || c == '-' && i > 0 && i + 1 < text.Length && char.IsLetter(text[i - 1]) && char.IsLetter(text[i + 1]);
                sb.Append(keep ? c : ' ');
            }
            return string.Join(" ", sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        // Что Whisper пишет на тишину и шум: титры с YouTube, на которых он учился
        private static readonly string[] JunkPhrases =
        {
            "продолжение следует", "субтитр", "семкин", "егорова", "спасибо за просмотр", "подписывайтесь на канал",
            "ставьте лайк", "смотрите в следующ", "amara", "dimatorzok", "игорь негода", "благодарю за внимание",
        };

        /// <summary>
        /// Почему текст — галлюцинация (null — нормальный): пусто, титры, повторы по кругу,
        /// слишком много слов для такой короткой фразы, кусок подсказки.
        /// </summary>
        public static string Junk(string raw, double seconds)
        {
            var text = Clean(raw);
            if (text.Length == 0)
                return "пусто";
            var lower = (raw ?? "").ToLowerInvariant().Replace('ё', 'е');
            foreach (var junk in JunkPhrases)
            {
                if (lower.Contains(junk))
                    return "титры: " + junk;
            }
            // Живая речь — до ~20 букв в секунду, а зацикленный Whisper пишет много и быстро
            if (text.Length > 12 && text.Length > 25 * Math.Max(1.0, seconds))
                return "слишком длинно для " + seconds.ToString("0.0") + " с";
            var words = text.Split(' ');
            // «Взлетай взлетай взлетай» — можно, а одно слово 6 раз подряд или фраза по кругу — нет
            for (int i = 0, run = 1; i + 1 < words.Length; i++)
            {
                run = words[i] == words[i + 1] ? run + 1 : 1;
                if (run > 5)
                    return "повтор слова";
            }
            for (var n = 2; n <= 4; n++)
            {
                for (var i = 0; i + 3 * n <= words.Length; i++)
                {
                    var same = true;
                    for (var k = 0; k < n && same; k++)
                        same = words[i + k] == words[i + n + k] && words[i + k] == words[i + 2 * n + k];
                    if (same)
                        return "повтор фразы";
                }
            }
            // Пересказал подсказку (бывает на тишине): 5+ слов подряд из неё
            if (words.Length >= 5 && promptClean.Length > 0 && (" " + promptClean + " ").Contains(" " + text + " "))
                return "кусок подсказки";
            return null;
        }

        private static string promptClean = "";

        /// <summary>
        /// Текст Whisper, готовый для колонки: если Vosk слышал «алиса», она будет и тут
        /// (Whisper иногда пишет «Алис», «Лиса» или теряет обращение).
        /// </summary>
        public static string WithWake(string text, bool wake, string wakeWord = "алиса")
        {
            if (text == null || !wake || text.Contains(wakeWord))
                return text;
            var words = text.Split(' ').ToList();
            for (var i = 0; i < words.Count && i < 3; i++)
            {
                if (words[i].Length >= 3 && Levenshtein(words[i], wakeWord) <= 2)
                {
                    words[i] = wakeWord;
                    return string.Join(" ", words);
                }
            }
            return wakeWord + " " + text;
        }

        private static int Levenshtein(string a, string b)
        {
            var d = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++)
                d[j] = j;
            for (var i = 1; i <= a.Length; i++)
            {
                var prev = d[0];
                d[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var t = d[j];
                    d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                    prev = t;
                }
            }
            return d[b.Length];
        }

        /// <summary>
        /// Подсказка для Whisper: как пишутся имена друзей и луны, и что это разговор с Алисой. Каждый токен
        /// подсказки — время на процессоре (~200 токенов ≈ +1.5 с), поэтому коротко: луны и имена, без
        /// предметов и падежей (обычные слова Whisper и так знает). Падежи и звучания разбирает AliceIntents.
        /// </summary>
        /// <param name="names">Как друзей зовут вслух (все формы из SpokenNames).</param>
        /// <param name="intents">Текст intents.txt: строки «@Луна: как говорят, …».</param>
        public static string BuildPrompt(IEnumerable<string> names, string intents)
        {
            var moons = new List<string>();
            foreach (var raw in (intents ?? "").Split('\n'))
            {
                var line = raw.Trim();
                var colon = line.IndexOf(':');
                if (colon < 0 || line.Length < 2 || line[0] != '@')
                    continue;
                var first = Capital(line.Substring(colon + 1).Split(',')[0].Trim());
                if (first.Length > 0 && first != "Ренд" && !moons.Contains(first))
                    moons.Add(first);
            }
            var friends = (names ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => Capital(n.Trim())).Distinct().Take(16).ToList();
            var sb = new StringBuilder("Алиса, полетели на Ренд. ");
            if (moons.Count > 0)
                sb.Append("Луны: ").Append(string.Join(", ", moons.Take(16))).Append(". ");
            if (friends.Count > 0)
                sb.Append("Где ").Append(string.Join(", ", friends)).Append("? ");
            sb.Append("Расскажи анекдот. Какая погода, квота? Сколько я принёс? Открой дверь бэ четыре. Включи свет. Купи фонарик. Взлетаем, запускай, погнали!");
            Prompt = sb.ToString();
            promptClean = Clean(Prompt);
            return Prompt;
        }

        private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        // ---- Звук ----

        /// <summary>Моно short любой частоты → float 16 кГц (среднее по окну — заодно фильтр от наложения).</summary>
        public static float[] Resample(short[] pcm, int count, int rate)
        {
            var n = (int)((long)count * Rate / rate);
            var output = new float[n];
            var step = rate / (double)Rate;
            var peak = 0f;
            for (var i = 0; i < n; i++)
            {
                var from = (int)(i * step);
                var to = Math.Min(count, Math.Max(from + 1, (int)((i + 1) * step)));
                var sum = 0f;
                for (var j = from; j < to; j++)
                    sum += pcm[j];
                var v = sum / (to - from) / 32768f;
                output[i] = v;
                peak = Math.Max(peak, Math.Abs(v));
            }
            // Тихий микрофон: поднимаем громкость (не больше чем в 8 раз)
            if (peak > 0f && peak < 0.5f)
            {
                var gain = Math.Min(8f, 0.5f / peak);
                for (var i = 0; i < n; i++)
                    output[i] *= gain;
            }
            return output;
        }

        // ---- Нативные библиотеки ----

        // Mono ищет whisper.dll рядом с игрой: загружаем её и зависимости по полному пути заранее,
        // DllImport("whisper") найдёт уже загруженную. Сначала Vulkan (если есть драйвер), потом процессор.
        private static string LoadNatives()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                return "CPU"; // тест на Linux: libwhisper.so рядом с тестом
            var cpu = new[] { "ggml-base-whisper.dll", "ggml-cpu-whisper.dll", "ggml-whisper.dll", "whisper.dll" };
            var vulkan = new[] { "ggml-base-whisper.dll", "ggml-cpu-whisper.dll", "ggml-vulkan-whisper.dll", "ggml-whisper.dll", "whisper.dll" };
            if (UseGpu && LoadLibraryW("vulkan-1.dll") != IntPtr.Zero)
            {
                try
                {
                    LoadAll(Path.Combine(Dir, "vulkan"), vulkan);
                    return "Vulkan";
                }
                catch (Exception e)
                {
                    LogWarning?.Invoke("Алиса: Whisper без видеокарты: " + e.Message);
                }
            }
            LoadAll(Path.Combine(Dir, "cpu"), cpu);
            return "CPU";
        }

        private static void LoadAll(string dir, string[] names)
        {
            foreach (var name in names)
            {
                var path = Path.Combine(dir, name);
                // LOAD_WITH_ALTERED_SEARCH_PATH: зависимости ищутся в папке самой библиотеки
                if (LoadLibraryExW(path, IntPtr.Zero, 0x8) == IntPtr.Zero)
                    throw new FileNotFoundException($"Не загрузилась {path} (ошибка {Marshal.GetLastWin32Error()})");
            }
        }

        private static IntPtr Utf8(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            var ptr = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            Marshal.WriteByte(ptr, bytes.Length, 0);
            return ptr;
        }

        private static string FromUtf8(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero)
                return "";
            var length = 0;
            while (Marshal.ReadByte(ptr, length) != 0)
                length++;
            var bytes = new byte[length];
            Marshal.Copy(ptr, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);

        // ---- C API whisper.cpp 1.8.x (whisper.h), раскладка структур как в C на x64 ----

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool AbortCallback(IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        internal struct ContextParams
        {
            public byte UseGpu;
            public byte FlashAttn;
            public int GpuDevice;
            public byte DtwTokenTimestamps;
            public int DtwAheadsPreset;
            public int DtwNTop;
            public UIntPtr DtwAheadsNHeads;
            public IntPtr DtwAheadsHeads;
            public UIntPtr DtwMemSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FullParams
        {
            public int Strategy;
            public int NThreads;
            public int NMaxTextCtx;
            public int OffsetMs;
            public int DurationMs;
            public byte Translate;
            public byte NoContext;
            public byte NoTimestamps;
            public byte SingleSegment;
            public byte PrintSpecial;
            public byte PrintProgress;
            public byte PrintRealtime;
            public byte PrintTimestamps;
            public byte TokenTimestamps;
            public float TholdPt;
            public float TholdPtsum;
            public int MaxLen;
            public byte SplitOnWord;
            public int MaxTokens;
            public byte DebugMode;
            public int AudioCtx;
            public byte TdrzEnable;
            public IntPtr SuppressRegex;
            public IntPtr InitialPrompt;
            public byte CarryInitialPrompt;
            public IntPtr PromptTokens;
            public int PromptNTokens;
            public IntPtr Language;
            public byte DetectLanguage;
            public byte SuppressBlank;
            public byte SuppressNst;
            public float Temperature;
            public float MaxInitialTs;
            public float LengthPenalty;
            public float TemperatureInc;
            public float EntropyThold;
            public float LogprobThold;
            public float NoSpeechThold;
            public int GreedyBestOf;
            public int BeamSize;
            public float BeamPatience;
            public IntPtr NewSegmentCallback;
            public IntPtr NewSegmentCallbackUserData;
            public IntPtr ProgressCallback;
            public IntPtr ProgressCallbackUserData;
            public IntPtr EncoderBeginCallback;
            public IntPtr EncoderBeginCallbackUserData;
            public IntPtr AbortCallback;
            public IntPtr AbortCallbackUserData;
            public IntPtr LogitsFilterCallback;
            public IntPtr LogitsFilterCallbackUserData;
            public IntPtr GrammarRules;
            public UIntPtr NGrammarRules;
            public UIntPtr IStartRule;
            public float GrammarPenalty;
            public byte Vad;
            public IntPtr VadModelPath;
            public float VadThreshold;
            public int VadMinSpeechDurationMs;
            public int VadMinSilenceDurationMs;
            public float VadMaxSpeechDurationS;
            public int VadSpeechPadMs;
            public float VadSamplesOverlap;
        }

        [DllImport("whisper", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr whisper_context_default_params_by_ref();

        [DllImport("whisper", CallingConvention = CallingConvention.Cdecl)]
        private static extern void whisper_free_context_params(IntPtr p);

        [DllImport("whisper", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr whisper_init_from_buffer_with_params(IntPtr buffer, UIntPtr size, ContextParams p);

        [DllImport("whisper", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr whisper_full_default_params_by_ref(int strategy);

        [DllImport("whisper", CallingConvention = CallingConvention.Cdecl)]
        private static extern void whisper_free_params(IntPtr p);

        [DllImport("whisper", CallingConvention = CallingConvention.Cdecl)]
        private static extern int whisper_full(IntPtr ctx, FullParams p, float[] samples, int count);

        [DllImport("whisper", CallingConvention = CallingConvention.Cdecl)]
        private static extern int whisper_full_n_segments(IntPtr ctx);

        [DllImport("whisper", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr whisper_full_get_segment_text(IntPtr ctx, int segment);

        /// <summary>Для теста: размеры структур как их видит C#.</summary>
        internal static int SizeOfContextParams => Marshal.SizeOf(typeof(ContextParams));
        internal static int SizeOfFullParams => Marshal.SizeOf(typeof(FullParams));
        internal static int OffsetOf(string field) => Marshal.OffsetOf(typeof(FullParams), field).ToInt32();
    }
}
