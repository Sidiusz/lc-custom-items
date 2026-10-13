#!/usr/bin/env python3
"""Ужимает vosk-model-small-ru-0.22 для Алисы: вместо языковой модели Gr.fst (32 МБ) — крошечный Gr.fst
с одной таблицей слов и words.json (список слов, его плагин отдаёт Vosk как грамматику).
Vosk из Gr.fst при грамматике берёт только номера слов, так что распознаёт так же («Алиса» на 213 фразах:
3 из 3, ложных 0), только быстрее.
  small-gr.py <words.txt из Gr.fst исходной модели> <список слов, по одному в строке> <папка модели>
words.txt достаётся из таблицы символов исходного Gr.fst (OpenFst: fstprint --save_osymbols)."""
import json, struct, sys

table = dict(l.split() for l in open(sys.argv[1], encoding='utf-8'))
words = [w for w in open(sys.argv[2], encoding='utf-8').read().split()
         if w in table and not w.startswith(('<', '#')) and w != '!SIL']
words = list(dict.fromkeys(words)) + ['[unk]']
syms = [('<eps>', 0)] + [(w, int(table[w])) for w in words]


def s(x):
    b = x.encode()
    return struct.pack('<i', len(b)) + b


def symtab():
    out = struct.pack('<i', 2125658996) + s('words') + struct.pack('<qq', max(i for _, i in syms) + 1, len(syms))
    for w, i in syms:
        out += s(w) + struct.pack('<q', i)
    return out


# VectorFst: заголовок, обе таблицы символов, одно состояние без дуг
header = (struct.pack('<i', 2125659606) + s('vector') + s('standard') + struct.pack('<ii', 2, 3)
          + struct.pack('<Q', 0) + struct.pack('<qqq', 0, 1, 0))
open(sys.argv[3] + '/graph/Gr.fst', 'wb').write(header + symtab() + symtab() + struct.pack('<f', 0.0) + struct.pack('<q', 0))
json.dump(words, open(sys.argv[3] + '/words.json', 'w', encoding='utf-8'), ensure_ascii=False)
print(len(words), 'слов')
