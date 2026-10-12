# BERT-модель Hugging Face (model.safetensors, config.json, vocab.txt) -> GGUF для llama.cpp, без torch.
# Нужно: pip install gguf numpy. Модель Алисы (CustomItems/AliceSemantic/rubert-mini-frida-f16.gguf):
#   скачать с huggingface.co/sergeyzh/rubert-mini-frida (ревизия 32a82388) config.json, model.safetensors, vocab.txt
#   python3 bert2gguf.py <папка> rubert-mini-frida-f16.gguf f16 mean --wpm
# q8 квантует только матрицы с шириной кратной 32 (у rubert-mini-frida 312 — остаётся f16).
# --wpm: словарь WordPiece из vocab.txt (токены режет сам мод, см. AliceSemantic.WordPiece);
# --tok-from <gguf>: взять словарь SentencePiece из другого GGUF с тем же словарём (так делали multilingual-e5-small).
# Проверить, что вектор совпадает с исходной моделью: verify.py <папка> <кеш AliceSemantic>.
import json, struct, sys, os
import numpy as np
import gguf
from gguf import GGUFReader, GGUFWriter, GGMLQuantizationType
from gguf.quants import quantize

src, out, qtype, pooling = sys.argv[1:5]
mode = sys.argv[5]
cfg = json.load(open(os.path.join(src, 'config.json')))

def load_safetensors(path):
    with open(path, 'rb') as f:
        n = struct.unpack('<Q', f.read(8))[0]
        header = json.loads(f.read(n))
        base = 8 + n
    mm = np.memmap(path, dtype=np.uint8, mode='r')
    res = {}
    for k, v in header.items():
        if k == '__metadata__':
            continue
        dt = {'F32': np.float32, 'F16': np.float16, 'I64': np.int64}[v['dtype']]
        a, b = v['data_offsets']
        raw = mm[base + a: base + b]
        arr = np.frombuffer(raw, dtype=dt).reshape(v['shape'])
        res[k] = arr.astype(np.float32)
        if v['dtype'] == 'I64': del res[k]
    return res

t = load_safetensors(os.path.join(src, 'model.safetensors'))
t = {k[5:] if k.startswith('bert.') else k: v for k, v in t.items()}

def name(k):
    m = {
        'embeddings.word_embeddings.weight': 'token_embd.weight',
        'embeddings.position_embeddings.weight': 'position_embd.weight',
        'embeddings.token_type_embeddings.weight': 'token_types.weight',
        'embeddings.LayerNorm.weight': 'token_embd_norm.weight',
        'embeddings.LayerNorm.bias': 'token_embd_norm.bias',
    }
    if k in m:
        return m[k]
    if not k.startswith('encoder.layer.'):
        return None
    rest = k[len('encoder.layer.'):]
    i, rest = rest.split('.', 1)
    sub = {
        'attention.self.query': 'attn_q', 'attention.self.key': 'attn_k', 'attention.self.value': 'attn_v',
        'attention.output.dense': 'attn_output', 'attention.output.LayerNorm': 'attn_output_norm',
        'intermediate.dense': 'ffn_up', 'output.dense': 'ffn_down', 'output.LayerNorm': 'layer_output_norm',
    }
    mod, wb = rest.rsplit('.', 1)
    return f'blk.{i}.{sub[mod]}.{wb}'

w = GGUFWriter(out, 'bert')
w.add_name(os.path.basename(src.rstrip('/')))
w.add_context_length(cfg['max_position_embeddings'])
w.add_embedding_length(cfg['hidden_size'])
w.add_feed_forward_length(cfg['intermediate_size'])
w.add_block_count(cfg['num_hidden_layers'])
w.add_head_count(cfg['num_attention_heads'])
w.add_layer_norm_eps(cfg.get('layer_norm_eps', 1e-12))
w.add_causal_attention(False)
w.add_pooling_type(gguf.PoolingType.MEAN if pooling == 'mean' else gguf.PoolingType.CLS)
w.add_file_type(gguf.LlamaFileType.MOSTLY_Q8_0 if qtype == 'q8' else gguf.LlamaFileType.MOSTLY_F16)

if mode == '--tok-from':
    r = GGUFReader(sys.argv[6])
    for f in r.fields.values():
        if not f.name.startswith('tokenizer.'):
            continue
        tp = f.types[0]
        if tp == gguf.GGUFValueType.ARRAY:
            sub = f.types[1]
            if sub == gguf.GGUFValueType.STRING:
                val = [bytes(f.parts[i]) for i in f.data]
            else:
                val = [f.parts[i][0].item() for i in f.data]
            w.add_key_value(f.name, val, gguf.GGUFValueType.ARRAY, sub_type=sub)
        elif tp == gguf.GGUFValueType.STRING:
            w.add_string(f.name, bytes(f.parts[f.data[0]]).decode('utf8'))
        else:
            w.add_key_value(f.name, f.parts[f.data[0]][0].item(), tp)
    w.add_token_type_count(cfg.get('type_vocab_size', 1))
else:
    vocab = [l.rstrip('\n') for l in open(os.path.join(src, 'vocab.txt'), encoding='utf8')]
    def phantom(tok):
        if tok.startswith('[') and tok.endswith(']'):
            return tok
        if tok.startswith('##'):
            return tok[2:]
        return '▁' + tok
    types = [gguf.TokenType.CONTROL if (v.startswith('[') and v.endswith(']')) else gguf.TokenType.NORMAL for v in vocab]
    unk = vocab.index('[UNK]')
    types[unk] = gguf.TokenType.UNKNOWN
    w.add_tokenizer_model('bert')
    w.add_tokenizer_pre('default')
    w.add_token_list([phantom(v) for v in vocab])
    w.add_token_types(types)
    w.add_token_type_count(cfg.get('type_vocab_size', 2))
    w.add_bos_token_id(vocab.index('[CLS]'))
    w.add_eos_token_id(vocab.index('[SEP]'))
    w.add_sep_token_id(vocab.index('[SEP]'))
    w.add_unk_token_id(unk)
    w.add_pad_token_id(vocab.index('[PAD]'))
    w.add_mask_token_id(vocab.index('[MASK]'))
    w.add_add_bos_token(True)
    w.add_add_eos_token(True)

for k, v in t.items():
    n = name(k)
    if n is None:
        print('skip', k)
        continue
    if n == 'token_types.weight' and mode == '--tok-from':
        pass
    if v.ndim == 2 and qtype == 'q8' and v.shape[1] % 32 == 0 and n != 'token_types.weight' and n != 'position_embd.weight':
        q = quantize(v, GGMLQuantizationType.Q8_0)
        w.add_tensor(n, q, raw_dtype=GGMLQuantizationType.Q8_0)
    elif v.ndim == 2 and n not in ('token_types.weight', 'position_embd.weight'):
        w.add_tensor(n, v.astype(np.float16))
    else:
        w.add_tensor(n, v.astype(np.float32))

w.write_header_to_file()
w.write_kv_data_to_file()
w.write_tensors_to_file()
w.close()
print('ok', out, os.path.getsize(out))
