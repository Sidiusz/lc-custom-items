using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

// args: model.gguf intents.txt tests.tsv [off.txt] [threads]
var model = args[0];
var intents = File.ReadAllLines(args[1]);
var tests = File.ReadAllLines(args[2]).Where(l => l.Contains('\t')).Select(l => l.Split('\t')).ToList();
var off = args.Length > 3 && File.Exists(args[3]) ? File.ReadAllLines(args[3]).Where(l => l.Trim().Length > 0).ToList() : new List<string>();
var threads = args.Length > 4 ? int.Parse(args[4]) : 4;
var nEx = int.TryParse(Environment.GetEnvironmentVariable("EXAMPLES"), out var ne) ? ne : 4;

// Темы и несколько примеров каждой
var topics = new List<(string code, string topic, string[] ex)>();
foreach (var raw in intents)
{
    var line = raw.Trim();
    if (line.Length == 0 || line.StartsWith("# ") || line.Contains(">") || line.StartsWith("$") || line.StartsWith("@")) continue;
    var c = line.IndexOf(':');
    if (c <= 0) continue;
    var topic = line.Substring(0, c).Trim();
    var ex = line.Substring(c + 1).Split(',').Select(s => s.Trim().Replace("{name}", "Андрей").Replace("{moon}", "Титан").Replace("{item}", "фонарик").Replace("{number}", "два"))
        .Where(s => s.Length > 0 && !s.Contains("{")).Take(nEx).ToArray();
    var code = topic.TrimStart('#').Replace('/', '_').ToLowerInvariant();
    topics.Add((code, topic, ex));
}
topics.Add(("none", null, new[] { "игроки говорят между собой", "непонятный шум" }));

var rerank = int.TryParse(Environment.GetEnvironmentVariable("RERANK"), out var rk) ? rk : 0;
if (rerank > 0)
{
    var it = string.Join("\n", intents);
    CustomItems.Items.AliceIntents.Load(it);
    var names = new Dictionary<string, string[]> { ["Daniil"] = new[] { "даниил", "данил", "даня" }, ["Andrey"] = new[] { "андрей", "андрюха" },
        ["Arseniy"] = new[] { "арсений", "сеня" }, ["Pavel"] = new[] { "павел", "паша", "пашка" } };
    CustomItems.Items.AliceIntents.SetNames(names);
    CustomItems.AliceSemantic.IsName = w => CustomItems.Items.AliceIntents.NameIn(w) != null;
    CustomItems.AliceSemantic.IsMoon = w => CustomItems.Items.AliceIntents.MoonIn(w) != null;
    if (!CustomItems.AliceSemantic.LoadNow("", "../../CustomItems/AliceSemantic/rubert-mini-frida-f16.gguf", "/tmp/alice-semantic/cache.bin", new[] { it }, new List<KeyValuePair<string, string>>()))
        throw new Exception("semantic");
}
var sb = new StringBuilder();
sb.Append("<|im_start|>system\nТы — голосовой помощник Алиса на корабле в игре Lethal Company. Игрок обращается к тебе. " +
          "Определи, что он хочет, и ответь одним кодом темы из списка. Имена игроков и предметы могут быть любыми. " +
          "Если фраза не к тебе или непонятна — none.\nТемы (код: примеры):\n");
if (rerank == 0)
    foreach (var t in topics) sb.Append(t.code).Append(": ").Append(string.Join("; ", t.ex)).Append('\n');
else
    sb.Append("(список дан в вопросе)\n");
sb.Append("<|im_end|>\n");
var prefix = sb.ToString();

N.llama_log_set(N.Quiet, IntPtr.Zero);
N.llama_backend_init();
var mp = N.llama_model_default_params();
mp.n_gpu_layers = 0;
var m = N.llama_model_load_from_file(Encoding.UTF8.GetBytes(model + "\0"), mp);
var vocab = N.llama_model_get_vocab(m);
var cp = N.llama_context_default_params();
cp.n_ctx = 8192; cp.n_batch = 8192; cp.n_ubatch = 512; cp.n_seq_max = 1;
cp.n_threads = threads; cp.n_threads_batch = threads;
var ctx = N.llama_init_from_model(m, cp);
var mem = N.llama_get_memory(ctx);
var nVocab = N.llama_vocab_n_tokens(vocab);
var eos = 151645; // <|im_end|> у Qwen2.5

int[] Tok(string s, bool special = true)
{
    var b = Encoding.UTF8.GetBytes(s);
    var t = new int[b.Length + 16];
    var n = N.llama_tokenize(vocab, b, b.Length, t, t.Length, false, special);
    return t.Take(n).ToArray();
}
var batch = N.llama_batch_init(8192, 0, 1);
int pos = 0;
unsafe void Feed(int[] toks, bool lastLogits)
{
    for (var i = 0; i < toks.Length; i += 512)
    {
        var chunk = toks.Skip(i).Take(512).ToArray();
        batch.n_tokens = chunk.Length;
        for (var j = 0; j < chunk.Length; j++)
        {
            ((int*)batch.token)[j] = chunk[j];
            ((int*)batch.pos)[j] = pos + j;
            ((int*)batch.n_seq_id)[j] = 1;
            ((int**)batch.seq_id)[j][0] = 0;
            ((byte*)batch.logits)[j] = (byte)(lastLogits && i + j == toks.Length - 1 ? 1 : 0);
        }
        if (N.llama_decode(ctx, batch) != 0) throw new Exception("decode");
        pos += chunk.Length;
    }
}

var sw = Stopwatch.StartNew();
var prefixTok = Tok(prefix);
Feed(prefixTok, false);
var prefixLen = pos;
Console.WriteLine($"prefix {prefixLen} tokens, {sw.ElapsedMilliseconds} ms");

// Коды тем — токенами, с концом <|im_end|>
var codeTok = topics.Select(t => Tok(t.code, false).Append(eos).ToArray()).ToList();

unsafe string Classify(string q, out double ms)
{
    var s = Stopwatch.StartNew();
    N.llama_memory_seq_rm(mem, 0, prefixLen, -1);
    pos = prefixLen;
    var alive = Enumerable.Range(0, topics.Count).ToList();
    var user = q;
    if (rerank > 0)
    {
        var fuzzy = CustomItems.Items.AliceIntents.Match(q, out _, out _);
        var cand = CustomItems.AliceSemantic.TopTopics(q, rerank);
        if (fuzzy != null && !cand.Contains(fuzzy)) cand.Insert(0, fuzzy);
        alive = topics.Select((t, i) => (t, i)).Where(x => x.t.topic == null || cand.Contains(x.t.topic)).Select(x => x.i).ToList();
        var u = new StringBuilder("Фраза игрока: «" + q + "»\nТемы (код: примеры):\n");
        foreach (var i in alive) u.Append(topics[i].code).Append(": ").Append(string.Join("; ", topics[i].ex)).Append('\n');
        u.Append("Какой код?");
        user = u.ToString();
    }
    Feed(Tok("<|im_start|>user\n" + user + "<|im_end|>\n<|im_start|>assistant\n"), true);
    var step = 0;
    while (true)
    {
        var logits = (float*)N.llama_get_logits_ith(ctx, -1);
        var allowed = alive.Where(i => codeTok[i].Length > step).Select(i => codeTok[i][step]).Distinct().ToList();
        var best = allowed.OrderByDescending(t => logits[t]).First();
        alive = alive.Where(i => codeTok[i].Length > step && codeTok[i][step] == best).ToList();
        if (best == eos || alive.Count == 1) break;
        Feed(new[] { best }, true);
        step++;
    }
    ms = s.Elapsed.TotalMilliseconds;
    return topics[alive[0]].topic;
}

bool Good(string got, string exp) => exp.Split('|').Contains(got ?? "-");
int ok = 0, fp = 0; var lat = new List<double>();
foreach (var t in tests)
{
    var got = Classify(t[0].ToLowerInvariant(), out var ms); lat.Add(ms);
    var g = Good(got, t[1]); ok += g ? 1 : 0;
    if (!g || Environment.GetEnvironmentVariable("VERBOSE") != null) Console.WriteLine($"{(g ? "ok " : "BAD")} «{t[0]}» хотим {t[1]}, LLM {got ?? "-"} ({ms:0} мс)");
}
foreach (var o in off)
{
    var got = Classify(o.ToLowerInvariant(), out var ms); lat.Add(ms);
    if (got != null) { fp++; Console.WriteLine($"FP  «{o}» → {got}"); }
}
lat.Sort();
Console.WriteLine($"вопросов {tests.Count}: верно {ok}; не к колонке {off.Count}, ложных {fp}; задержка медиана {lat[lat.Count / 2]:0} мс, p95 {lat[(int)(lat.Count * 0.95)]:0} мс");

static unsafe class N
{
    const string L = "llama";
    [StructLayout(LayoutKind.Sequential)] public struct ModelParams { public IntPtr devices, tensor_buft_overrides; public int n_gpu_layers, split_mode, main_gpu; public IntPtr tensor_split, progress_callback, progress_callback_user_data, kv_overrides; public byte vocab_only, use_mmap, use_direct_io, use_mlock, check_tensors, use_extra_bufts, no_host, no_alloc; }
    [StructLayout(LayoutKind.Sequential)] public struct ContextParams { public uint n_ctx, n_batch, n_ubatch, n_seq_max; public int n_threads, n_threads_batch; public int rope_scaling_type, pooling_type, attention_type, flash_attn_type; public float rope_freq_base, rope_freq_scale, yarn_ext_factor, yarn_attn_factor, yarn_beta_fast, yarn_beta_slow; public uint yarn_orig_ctx; public float defrag_threshold; public IntPtr cb_eval, cb_eval_user_data; public int type_k, type_v; public IntPtr abort_callback, abort_callback_user_data; public byte embeddings, offload_kqv, no_perf, op_offload, swa_full, kv_unified; public IntPtr samplers; public UIntPtr n_samplers; }
    [StructLayout(LayoutKind.Sequential)] public struct Batch { public int n_tokens; public IntPtr token, embd, pos, n_seq_id, seq_id, logits; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void LogCallback(int level, IntPtr text, IntPtr userData);
    public static readonly LogCallback Quiet = (a, b, c) => { };
    [DllImport(L)] public static extern void llama_log_set(LogCallback cb, IntPtr u);
    [DllImport(L)] public static extern void llama_backend_init();
    [DllImport(L)] public static extern ModelParams llama_model_default_params();
    [DllImport(L)] public static extern ContextParams llama_context_default_params();
    [DllImport(L)] public static extern IntPtr llama_model_load_from_file(byte[] path, ModelParams p);
    [DllImport(L)] public static extern IntPtr llama_init_from_model(IntPtr model, ContextParams p);
    [DllImport(L)] public static extern IntPtr llama_model_get_vocab(IntPtr model);
    [DllImport(L)] public static extern int llama_tokenize(IntPtr vocab, byte[] text, int len, [Out] int[] tokens, int max, [MarshalAs(UnmanagedType.U1)] bool addSpecial, [MarshalAs(UnmanagedType.U1)] bool parseSpecial);
    [DllImport(L)] public static extern Batch llama_batch_init(int n, int embd, int nSeq);
    [DllImport(L)] public static extern int llama_decode(IntPtr ctx, Batch b);
    [DllImport(L)] public static extern IntPtr llama_get_logits_ith(IntPtr ctx, int i);
    [DllImport(L)] public static extern IntPtr llama_get_memory(IntPtr ctx);
    [DllImport(L)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool llama_memory_seq_rm(IntPtr mem, int seq, int p0, int p1);
    [DllImport(L)] public static extern int llama_vocab_n_tokens(IntPtr vocab);
}
