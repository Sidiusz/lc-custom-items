using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CustomItems;
using CustomItems.Items;

// Проверка понимания Алисы: по словам (AliceIntents), по смыслу (AliceSemantic) и вместе, как в игре.
// args: model.gguf intents.txt phrasesDir tests.tsv off.txt [cache.bin] [dump.tsv]
// tests.tsv: «вопрос<TAB>тема|другая допустимая тема[<TAB>Предмет×число]»; off.txt: фразы, на которые темы быть не должно.
// SEM_<поле>=значение в окружении меняет поле AliceSemantic (пороги, Threads, приставки).
var model = args[0];
var intentsText = File.ReadAllText(args[1]);
var phrasesDir = args[2];
var tests = File.ReadAllLines(args[3]).Where(l => l.Contains('\t')).Select(l => l.Split('\t')).ToList();
var off = File.ReadAllLines(args[4]).Where(l => l.Trim().Length > 0).ToList();
var cache = args.Length > 5 ? args[5] : null;
var dumpPath = args.Length > 6 ? args[6] : null;

AliceIntents.Load(intentsText);
AliceIntents.SetNames(new Dictionary<string, string[]>
{
    ["Daniil"] = new[] { "даниил", "данил", "даня", "сидиус" }, ["Andrey"] = new[] { "андрей", "андрюха", "андрюша", "синон" },
    ["Arseniy"] = new[] { "арсений", "сеня", "арсен", "шакабуй" }, ["Pavel"] = new[] { "павел", "паша", "пашка", "пашок" },
});
var phrases = Directory.GetFiles(phrasesDir, "*.wav", SearchOption.AllDirectories)
    .Select(f => new KeyValuePair<string, string>(Path.GetRelativePath(phrasesDir, Path.GetDirectoryName(f)).Replace('\\', '/'),
        Path.GetFileNameWithoutExtension(f))).ToList();

AliceSemantic.Log = Console.WriteLine;
AliceSemantic.LogWarning = s => Console.WriteLine("WARN " + s);
AliceSemantic.IsName = w => AliceIntents.NameIn(w) != null;
AliceSemantic.IsMoon = w => AliceIntents.MoonIn(w) != null;
foreach (var kv in Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>())
{
    var k = (string)kv.Key;
    if (!k.StartsWith("SEM_"))
        continue;
    var f = typeof(AliceSemantic).GetField(k.Substring(4));
    f.SetValue(null, Convert.ChangeType(kv.Value, f.FieldType, System.Globalization.CultureInfo.InvariantCulture));
}

var sw = Stopwatch.StartNew();
if (!AliceSemantic.LoadNow("", model, cache, new[] { intentsText }, phrases))
    return 1;
Console.WriteLine($"load {sw.ElapsedMilliseconds} ms, RSS {Process.GetCurrentProcess().WorkingSet64 / 1048576} MB");

bool Good(string got, string exp) => got != null && exp.Split('|').Contains(got);
int F = 0, S = 0, C = 0, Ff = 0, Sf = 0, Cf = 0;
var lat = new List<double>();
var dump = new List<string>();
foreach (var t in tests.Concat(off.Select(o => new[] { o, "-" })))
{
    var q = t[0];
    var exp = t[1];
    var cmd = q.ToLowerInvariant();
    var fuzzy = AliceIntents.Match(cmd, out var fscore, out _);
    var s0 = Stopwatch.StartNew();
    var sem = AliceSemantic.Match(cmd, out var sscore, out var swhy);
    lat.Add(s0.Elapsed.TotalMilliseconds);
    var why = "";
    var comb = AliceSemantic.Combine(cmd, fuzzy, fscore, ref why);
    if (dumpPath != null)
    {
        var top = AliceSemantic.Explain(cmd, out var b1, out var t2, out var b2, out var pt, out var ps);
        dump.Add($"{exp}\t{top}\t{b1:0.000}\t{t2}\t{b2:0.000}\t{pt}\t{ps:0.000}\t{fuzzy}\t{fscore:0.00}\t{q}");
    }
    bool ok;
    if (exp == "-")
    {
        Ff += fuzzy != null ? 1 : 0;
        Sf += sem != null ? 1 : 0;
        Cf += comb != null ? 1 : 0;
        ok = comb == null;
    }
    else
    {
        F += Good(fuzzy, exp) ? 1 : 0;
        S += Good(sem, exp) ? 1 : 0;
        C += Good(comb, exp) ? 1 : 0;
        ok = Good(comb, exp);
        // Третий столбец у покупок: «Pro-flashlight×3» — какой предмет и сколько разобрано
        if (t.Length > 2 && t[2].Length > 0)
        {
            var bought = $"{AliceIntents.ItemIn(cmd)}×{AliceIntents.NumberIn(cmd)}";
            ok &= bought == t[2];
            swhy = $"покупка {bought}; " + swhy;
        }
    }
    if (!ok || Environment.GetEnvironmentVariable("VERBOSE") != null)
        Console.WriteLine($"{(ok ? "ok " : "BAD")} «{q}» хотим {exp}: слова {fuzzy ?? "-"} {fscore:0.00}, смысл {sem ?? "-"} {sscore:0.000}, вместе {comb ?? "-"} | {swhy}");
}
if (dumpPath != null)
    File.WriteAllLines(dumpPath, dump);
lat.Sort();
Console.WriteLine($"вопросов {tests.Count}: по словам {F}, по смыслу {S}, вместе {C}");
Console.WriteLine($"не к колонке {off.Count}, ложных: по словам {Ff}, по смыслу {Sf}, вместе {Cf}");
Console.WriteLine($"задержка смысла: медиана {lat[lat.Count / 2]:0.0} мс, p95 {lat[(int)(lat.Count * 0.95)]:0.0} мс, max {lat[^1]:0.0} мс; " +
                  $"RSS {Process.GetCurrentProcess().WorkingSet64 / 1048576} МБ");
return 0;
