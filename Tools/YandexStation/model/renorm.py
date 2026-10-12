# Перенормирует ARPA после фильтра словаря (иначе OpenGrm не смешивает модели).
import math, collections, sys
L10 = math.log(10)
src, dst = sys.argv[1], sys.argv[2]
uni, bi, tri = {}, {}, {}
sec = 0
for l in open(src, encoding='utf-8'):
    l = l.rstrip('\n')
    if l.startswith('\\') :
        if l[1:2].isdigit(): sec = int(l[1])
        continue
    if not l or l.startswith('ngram '): continue
    p = l.split('\t'); lp = float(p[0]); ws = tuple(p[1].split(' ')); bo = float(p[2]) if len(p) > 2 else 0.0
    (uni if sec == 1 else bi if sec == 2 else tri)[ws] = [lp, bo]
n0=len(tri)
tri = {k:v for k,v in tri.items() if k[:2] in bi and k[1:] in bi}
bi = {k:v for k,v in bi.items() if (k[0],) in uni and (k[1],) in uni}
print('dropped tri', n0-len(tri))
# unigrams: renormalize over all except <s>
z = sum(10**v[0] for k, v in uni.items() if k[0] != '<s>')
for k, v in uni.items():
    if k[0] != '<s>': v[0] -= math.log10(z)
def puni(w): return 10**uni[(w,)][0]
bys = collections.defaultdict(list)
for k in bi: bys[k[0]].append(k)
for u, ks in bys.items():
    num = 1 - sum(10**bi[k][0] for k in ks); den = 1 - sum(puni(k[1]) for k in ks)
    if num < 1e-4 or den < 1e-4:
        s = sum(10**bi[k][0] for k in ks)
        for k in ks: bi[k][0] += math.log10(0.999 / s)
        uni[(u,)][1] = math.log10(0.001 / max(den, 1e-9))
    else: uni[(u,)][1] = math.log10(num / den)
def pbi(v, w):
    if (v, w) in bi: return 10**bi[(v, w)][0]
    return 10**uni[(v,)][1] * puni(w)
tys = collections.defaultdict(list)
for k in tri: tys[k[:2]].append(k)
for h, ks in tys.items():
    num = 1 - sum(10**tri[k][0] for k in ks); den = 1 - sum(pbi(k[1], k[2]) for k in ks)
    if num < 1e-4 or den < 1e-4:
        s = sum(10**tri[k][0] for k in ks)
        for k in ks: tri[k][0] += math.log10(0.999 / s)
        bi[h][1] = math.log10(0.001 / max(den, 1e-9))
    else: bi[h][1] = math.log10(num / den)
# contexts without continuations: backoff 0
for u in uni:
    if u[0] not in bys and u[0] != '</s>': uni[u][1] = 0.0
with open(dst, 'w', encoding='utf-8') as o:
    o.write('\\data\\\n' + ''.join(f'ngram {n}={len(d)}\n' for n, d in ((1,uni),(2,bi),(3,tri))) + '\n')
    for n, d, hasbo in ((1, uni, True), (2, bi, True), (3, tri, False)):
        o.write(f'\\{n}-grams:\n')
        for k, (lp, bo) in d.items():
            o.write(f'{lp:.6f}\t{" ".join(k)}' + (f'\t{bo:.6f}' if hasbo and n < 3 and (n == 1 and k[0] in bys or n == 2 and k in tys) else '') + '\n')
        o.write('\n')
    o.write('\\end\\\n')
