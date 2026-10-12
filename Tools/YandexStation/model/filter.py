# Оставляет из общей русской LM (ru-small.lm.gz) K частых слов из словаря + слова корпуса.
import gzip, re, sys
K, C = int(sys.argv[1]), sys.argv[2]
dic = set()
for l in open(C + '/db/ru.dic', encoding='utf-8'):
    dic.add(l.split(' ',1)[0])
cyr = re.compile(r'^[а-яё-]+$')
f = gzip.open(C + '/db/ru-small.lm.gz','rt',encoding='utf-8')
uni = []
sec = None; lines = {1:[],2:[],3:[]}
allng = []
for l in f:
    l = l.rstrip('\n')
    m = re.match(r'^\\(\d)-grams:', l)
    if m: sec = int(m.group(1)); continue
    if not l or l.startswith('\\') or sec is None: continue
    p = l.split('\t')
    if sec == 1: uni.append((float(p[0]), p[1]))
    allng.append((sec, l, p[1].split(' ')))
uni.sort(reverse=True)
keep = {'<s>','</s>'}
for lp, w in uni:
    if len(keep) >= K: break
    if cyr.match(w) and w in dic: keep.add(w)
dom = set(open('domain.vocab', encoding='utf-8').read().split())
keep |= {w for w in dom if w in dic}
cnt = {1:0,2:0,3:0}; body = {1:[],2:[],3:[]}
for sec, l, ws in allng:
    if all(w in keep for w in ws):
        body[sec].append(l); cnt[sec]+=1
with open('general.arpa','w',encoding='utf-8') as o:
    o.write('\\data\\\n' + ''.join(f'ngram {n}={cnt[n]}\n' for n in (1,2,3)) + '\n')
    for n in (1,2,3):
        o.write(f'\\{n}-grams:\n' + '\n'.join(body[n]) + '\n\n')
    o.write('\\end\\\n')
print(cnt, len(keep))
