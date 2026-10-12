# Сверка: векторы из кеша AliceSemantic (llama.cpp) против прямого прогона исходной модели на numpy.
# verify.py <папка модели HF с tokenizer.json> </tmp/alice-semantic/cache.bin>; ждём косинус 1.00000.
# Нужно: pip install numpy tokenizers. Приставка «paraphrase: » уже в тексте кеша.
import json, struct, sys, math
import numpy as np
from tokenizers import Tokenizer
src, cache = sys.argv[1], sys.argv[2]
cfg = json.load(open(src + '/config.json'))

def load_safetensors(path):
    with open(path, 'rb') as f:
        n = struct.unpack('<Q', f.read(8))[0]; header = json.loads(f.read(n)); base = 8 + n
    mm = np.memmap(path, dtype=np.uint8, mode='r'); res = {}
    for k, v in header.items():
        if k == '__metadata__' or v['dtype'] != 'F32': continue
        a, b = v['data_offsets']
        res[k[5:] if k.startswith('bert.') else k] = np.frombuffer(mm[base + a: base + b], dtype=np.float32).reshape(v['shape'])
    return res
W = load_safetensors(src + '/model.safetensors')
def ln(x, w, b, eps=1e-12):
    m = x.mean(-1, keepdims=True); v = ((x - m) ** 2).mean(-1, keepdims=True); return (x - m) / np.sqrt(v + eps) * w + b
erf = np.vectorize(math.erf)
def gelu(x): return 0.5 * x * (1 + erf(x / math.sqrt(2)))
def lin(x, p): return x @ W[p + '.weight'].T + W[p + '.bias']
def embed(ids):
    ids = np.array(ids); n = len(ids)
    x = W['embeddings.word_embeddings.weight'][ids] + W['embeddings.position_embeddings.weight'][:n] + W['embeddings.token_type_embeddings.weight'][0]
    x = ln(x, W['embeddings.LayerNorm.weight'], W['embeddings.LayerNorm.bias'])
    H = cfg['num_attention_heads']; d = cfg['hidden_size'] // H
    for i in range(cfg['num_hidden_layers']):
        p = f'encoder.layer.{i}.'
        q = lin(x, p + 'attention.self.query').reshape(n, H, d).transpose(1, 0, 2)
        k = lin(x, p + 'attention.self.key').reshape(n, H, d).transpose(1, 0, 2)
        v = lin(x, p + 'attention.self.value').reshape(n, H, d).transpose(1, 0, 2)
        a = q @ k.transpose(0, 2, 1) / math.sqrt(d); a = np.exp(a - a.max(-1, keepdims=True)); a /= a.sum(-1, keepdims=True)
        o = (a @ v).transpose(1, 0, 2).reshape(n, -1)
        x = ln(x + lin(o, p + 'attention.output.dense'), W[p + 'attention.output.LayerNorm.weight'], W[p + 'attention.output.LayerNorm.bias'])
        h = gelu(lin(x, p + 'intermediate.dense'))
        x = ln(x + lin(h, p + 'output.dense'), W[p + 'output.LayerNorm.weight'], W[p + 'output.LayerNorm.bias'])
    e = x.mean(0); return e / np.linalg.norm(e)

def read_cache(path):
    f = open(path, 'rb')
    def rstr():
        n = 0; shift = 0
        while True:
            b = f.read(1)[0]; n |= (b & 0x7f) << shift; shift += 7
            if b < 0x80: break
        return f.read(n).decode('utf8')
    key = rstr(); cnt, dim = struct.unpack('<ii', f.read(8)); out = {}
    for _ in range(cnt):
        t = rstr(); out[t] = np.frombuffer(f.read(4 * dim), dtype=np.float32)
    return out
c = read_cache(cache)
tok = Tokenizer.from_file(src + '/tokenizer.json')
for t in list(c)[:3] + [k for k in c if 'ёжик' in k or 'йод' in k][:1] + list(c)[200:203]:
    ids = tok.encode(t).ids
    ref = embed(ids)
    print(f'{np.dot(ref, c[t]):.5f}  {t}')
