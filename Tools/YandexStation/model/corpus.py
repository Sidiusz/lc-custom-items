# Корпус для языковой модели Алисы: примеры из intents.txt со всеми лунами, предметами, числами и именами.
import re, random, sys
random.seed(1)
src = open(sys.argv[1], encoding='utf-8').read().split('\n')
moons, items, examples = [], [], []
for l in src:
    l = l.strip()
    if not l or l.startswith('# ') or l == '#': continue
    m = re.match(r'^(.+?):\s*(.*)$', l)
    if not m: continue
    head, body = m.groups()
    al = [a.strip().lower() for a in body.split(',') if a.strip()]
    if head.startswith('@'): moons.append([a for a in al if ' ' not in a])
    elif head.startswith('$'): items += al
    else: examples += al
numbers = "один одну два две пару три четыре пять шесть семь восемь девять десять одиннадцать двенадцать".split()
def forms(n):
    if n[-1] in 'ая':
        b = n[:-1]; return [n, b+'у' if n[-1]=='а' else b+'ю', b+('и' if b[-1] in 'кгхжшчщ' or n[-1]=='я' else 'ы'), b+('ей' if n[-1]=='я' or b[-1] in 'жшчщц' else 'ой'), b+'е']
    if n.endswith('й'): b=n[:-1]; return [n, b+'я', b+'ю', b+'ем', b+'е']
    return [n, n+'а', n+'у', n+'ом', n+'е']
names = "даниил данил даня сидиус андрей андрюха андрюша синон арсений сеня арсен шакабуй павел паша пашка пашок павлецки".split()
nameforms = sorted({f for n in names if n != 'павлецки' for f in forms(n)} - {'павела','павелу','павелом','павеле','пашока','пашоку','пашоком','пашоке'}
                   | {'павла','павлу','павлом','павле','павлецки','пашка','пашку','пашком','пашке'})
moonw = [a for g in moons for a in g]
out = []
def fill(e):
    e = e.replace('{moon}', random.choice(random.choice(moons)))
    e = e.replace('{item}', random.choice(items)).replace('{number}', random.choice(numbers)).replace('{name}', random.choice(nameforms))
    return e
for e in examples:
    reps = 30 if '{' in e else 3
    for _ in range(reps):
        s = fill(e)
        out.append(s); out.append('алиса ' + s)
for w in moonw + items + nameforms: out += [w, 'алиса ' + w]
out += ['алиса'] * 20
open('domain.txt','w',encoding='utf-8').write('\n'.join(out)+'\n')
vocab = sorted({w for s in out for w in s.split()})
open('domain.vocab','w',encoding='utf-8').write('\n'.join(vocab)+'\n')
print(len(out), len(vocab))
