// Vosk (как в игре: модель model-ru, без грамматики, кусками по 20 мс) против Whisper (AliceWhisper.cs).
// dotnet run -- <папка model-ru с распакованным final.mdl> <модель whisper .bin> <intents.txt> <phrases.txt> <папка со звуком> [префиксы файлов]
// Звук: <префикс><NN>.raw — s16le, 48 кГц, моно; NN — номер строки в phrases.txt.
// Переменные: THREADS (потоков Whisper), SHORT=0 (полное окно 30 с), MINCTX (наименьшее окно), NOPROMPT=1 (без подсказки).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CustomItems;
using CustomItems.Items;
using Vosk;

var voskDir = args[0];
var whisperModel = args[1];
var intents = File.ReadAllText(args[2]);
var truths = File.ReadAllLines(args[3]).Where(l => l.Trim().Length > 0).ToArray();
var audioDir = args[4];
var prefixes = args.Length > 5 ? args[5].Split(',') : new[] { "s", "e" };

// Структуры C API: размеры как в whisper.h (проверено offsetof на 1.8.4: 48 и 304 байта)
Console.WriteLine($"sizeof ContextParams {AliceWhisper.SizeOfContextParams} (C: 48), FullParams {AliceWhisper.SizeOfFullParams} (C: 304), " +
                  $"audio_ctx @{AliceWhisper.OffsetOf("AudioCtx")} (56), language @{AliceWhisper.OffsetOf("Language")} (104), " +
                  $"abort @{AliceWhisper.OffsetOf("AbortCallback")} (208), vad_params @{AliceWhisper.OffsetOf("VadThreshold")} (280)");

AliceIntents.Load(intents);
var names = new Dictionary<string, string[]>
{
    ["Daniil"] = new[] { "даниил", "данил", "даня", "сидиус" },
    ["Andrey"] = new[] { "андрей", "андрюха", "андрюша", "синон" },
    ["Arseniy"] = new[] { "арсений", "сеня", "арсен", "шакабуй" },
    ["Pavel"] = new[] { "павел", "паша", "пашка", "пашок", "павлецки" },
};
AliceIntents.SetNames(names);
AliceWhisper.BuildPrompt(names.Values.SelectMany(v => v), intents);
if (Environment.GetEnvironmentVariable("PROMPT") is string customPrompt)
    AliceWhisper.Prompt = customPrompt;
if (Environment.GetEnvironmentVariable("NOPROMPT") == "1")
    AliceWhisper.Prompt = "";
Console.WriteLine("Подсказка: " + AliceWhisper.Prompt);
if (int.TryParse(Environment.GetEnvironmentVariable("THREADS"), out var threads))
    AliceWhisper.Threads = threads;
AliceWhisper.ShortWindow = Environment.GetEnvironmentVariable("SHORT") != "0";
if (int.TryParse(Environment.GetEnvironmentVariable("MINCTX"), out var minCtx))
    AliceWhisper.MinWindow = minCtx;
AliceWhisper.TimeoutSeconds = 30f; // в тесте меряем, а не обрываем
AliceWhisper.Dir = Path.GetDirectoryName(Path.GetFullPath(whisperModel));
AliceWhisper.ModelFile = Path.GetFileName(whisperModel);
AliceWhisper.LogInfo = AliceWhisper.LogWarning = Console.WriteLine;
AliceWhisper.Load();

Vosk.Vosk.SetLogLevel(-1);
var model = new Model(voskDir);
var textField = new Regex("\"text\"\\s*:\\s*\"([^\"]*)\"");

string Norm(string s) => AliceWhisper.Clean(s).Replace("-", " ");
string Intent(string s)
{
    s = Norm(s);
    var wake = s.IndexOf("алиса", StringComparison.Ordinal);
    var command = wake >= 0 ? s.Substring(wake + 5).Trim() : s;
    if (command.Length == 0)
        return "-";
    var topic = AliceIntents.Match(command.Replace("unk", " "), out var score);
    if (topic != null && (topic == "Moon" || topic == "#route") && AliceIntents.MoonInText(command) is string moon)
        topic = "#route:" + moon;
    if (topic != null && topic == "#whereis" && AliceIntents.NameIn(command) is string who)
        topic += ":" + who;
    return topic ?? "?";
}
int Wer(string a, string b)
{
    var x = Norm(a).Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var y = Norm(b).Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var d = new int[x.Length + 1, y.Length + 1];
    for (var i = 0; i <= x.Length; i++) d[i, 0] = i;
    for (var j = 0; j <= y.Length; j++) d[0, j] = j;
    for (var i = 1; i <= x.Length; i++)
    for (var j = 1; j <= y.Length; j++)
        d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (x[i - 1] == y[j - 1] ? 0 : 1));
    return d[x.Length, y.Length];
}

foreach (var prefix in prefixes)
{
    int vExact = 0, wExact = 0, vIntent = 0, wIntent = 0, vErr = 0, wErr = 0, words = 0, junk = 0, n = 0;
    double vMs = 0, wMs = 0, wMax = 0, audioS = 0;
    var latencies = new List<double>();
    Console.WriteLine($"\n=== {prefix}: №, длина, Vosk мс, Whisper мс | правда | Vosk | Whisper");
    for (var k = 0; k < truths.Length; k++)
    {
        var file = Path.Combine(audioDir, $"{prefix}{k:00}.raw");
        if (!File.Exists(file))
            continue;
        var bytes = File.ReadAllBytes(file);
        var pcm = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, pcm, 0, bytes.Length);
        var seconds = pcm.Length / 48000.0;

        // Vosk: как AliceListener — кусками по 20 мс; время — только последний FinalResult (остальное шло в реальном времени)
        var heard = new List<string>();
        double vf;
        using (var rec = new VoskRecognizer(model, 48000f))
        {
            for (var i = 0; i < pcm.Length; i += 960)
            {
                var c = Math.Min(960, pcm.Length - i);
                var chunk = new short[c];
                Array.Copy(pcm, i, chunk, 0, c);
                if (rec.AcceptWaveform(chunk, c))
                    heard.Add(textField.Match(rec.Result()).Groups[1].Value);
            }
            var sw = Stopwatch.StartNew();
            heard.Add(textField.Match(rec.FinalResult()).Groups[1].Value);
            vf = sw.Elapsed.TotalMilliseconds;
            vMs += vf;
        }
        var vosk = string.Join(" ", heard.Where(h => h.Length > 0));

        var job = new AliceWhisper.Job { Samples = AliceWhisper.Resample(pcm, pcm.Length, 48000) };
        AliceWhisper.Run(job);
        var whisper = AliceWhisper.WithWake(job.Text, vosk.Contains("алиса")) ?? "";
        if (job.Text == null)
            junk++;
        var ms = job.Seconds * 1000;
        wMs += ms;
        wMax = Math.Max(wMax, ms);
        latencies.Add(ms);
        audioS += seconds;

        var truth = truths[k];
        var ti = Intent(truth);
        n++;
        words += Norm(truth).Split(' ').Length;
        vErr += Wer(truth, vosk);
        wErr += Wer(truth, whisper);
        if (Norm(vosk) == Norm(truth)) vExact++;
        if (Norm(whisper) == Norm(truth)) wExact++;
        var vi = Intent(vosk);
        var wi = Intent(whisper);
        if (vi == ti) vIntent++;
        if (wi == ti) wIntent++;
        Console.WriteLine($"{prefix}{k:00} {seconds:0.0}с {vf,4:0} {ms,5:0} | {truth} [{ti}] | {(Norm(vosk) == Norm(truth) ? "OK" : vosk)} [{vi}] | " +
                          $"{(Norm(whisper) == Norm(truth) ? "OK" : whisper + (job.Text == null ? $" (мусор: {job.Why}: «{job.Raw}»)" : ""))} [{wi}]");
    }
    latencies.Sort();
    Console.WriteLine($"ИТОГ {prefix}: фраз {n}, звука {audioS:0.0} с");
    Console.WriteLine($"  Vosk:    точно {vExact}/{n}, WER {100.0 * vErr / words:0.0}%, тема как у правды {vIntent}/{n}, FinalResult {vMs / n:0} мс в среднем");
    Console.WriteLine($"  Whisper: точно {wExact}/{n}, WER {100.0 * wErr / words:0.0}%, тема как у правды {wIntent}/{n}, " +
                      $"{wMs / n:0} мс в среднем, медиана {latencies[latencies.Count / 2]:0}, макс {wMax:0}, отброшено как мусор {junk}");
}
