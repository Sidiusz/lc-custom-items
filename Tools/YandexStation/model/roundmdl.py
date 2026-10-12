# Округляет веса модели (текстовый формат Kaldi) до N бит мантиссы: файл тот же, но жмётся в zip вдвое лучше.
import sys, numpy as np
bits = int(sys.argv[1]); drop = 23 - bits
mask = np.uint32((0xFFFFFFFF << drop) & 0xFFFFFFFF); half = np.uint32(1 << (drop - 1))
def rnd(tok):
    a = np.array(tok, dtype=np.float32).view(np.uint32)
    a = ((a + half) & mask).view(np.float32)
    return ' '.join('%.9g' % x for x in a)
n = 0
with open(sys.argv[2]) as f, open(sys.argv[3], 'w') as o:
    for l in f:
        s = l.strip(); tail = ''
        if s.endswith(']'): s = s[:-1].strip(); tail = ' ]'
        parts = s.split()
        if len(parts) > 8:
            try:
                o.write('  ' + rnd([float(p) for p in parts]) + tail + '\n'); n += 1; continue
            except ValueError: pass
        o.write(l)
print('rounded lines', n)
