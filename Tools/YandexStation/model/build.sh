#!/bin/bash
# Собирает модель распознавания Алисы (CustomItems/Vosk/model-ru) на Linux.
# Акустика — из vosk-model-ru-0.42 (большая, точная), граф — свой: 60 тыс. частых русских слов
# + всё из intents.txt (луны «асуранс», «офенс», предметы, имена) с английскими названиями в словаре.
# Нужно: Kaldi (src/bin, src/fstbin, src/nnet3bin; OpenFst с --enable-lookahead-fsts --enable-ngram-fsts),
# OpenGrm NGram, phonetisaurus (pip), распакованный vosk-model-ru-0.42-compile.
#   KALDI=~/kaldi GRM=~/opengrm/bin COMPILE=~/vosk-model-ru-0.42-compile ./build.sh
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd); REPO=$(cd "$HERE/../../.." && pwd)
: "${KALDI:?}" "${GRM:?}" "${COMPILE:?}"
W=${WORK:-/tmp/alice-model-build}; OUT=$REPO/CustomItems/Vosk/model-ru
export PATH=$GRM:$KALDI/tools/openfst/bin:$KALDI/src/bin:$KALDI/src/fstbin:$KALDI/src/nnet3bin:$PATH
export LD_LIBRARY_PATH=$KALDI/tools/openfst/lib:$KALDI/src/lib:${LD_LIBRARY_PATH:-}
mkdir -p "$W" && cd "$W"

# 1. Языковая модель: корпус из intents.txt + урезанная общая, смешаны 0.1/0.9 (общая/своя): команды узнаются лучше, обычная речь почти так же
python3 "$HERE/corpus.py" "$REPO/CustomItems/intents.txt"
python3 "$HERE/filter.py" 60000 "$COMPILE"
python3 "$HERE/renorm.py" general.arpa general-n.arpa
(awk '/^\\1-grams:/{f=1;next} /^\\2-grams:/{f=0} f&&NF{print $2}' general-n.arpa; cat domain.vocab) | sort -u | grep -v '^</\?s>$' > words.list
(echo "<eps> 0"; awk '{print $1, NR}' words.list; echo "<unk> $(( $(wc -l < words.list) + 1 ))") > syms.txt
ngramread --ARPA --symbols=syms.txt general-n.arpa general.fst
ngramshrink --method=relative_entropy --target_number_of_ngrams=500000 general.fst generalp.fst
farcompilestrings --fst_type=compact --symbols=syms.txt --keep_symbols --unknown_symbol="<unk>" domain.txt domain.far
ngramcount --order=3 domain.far domain.cnt
ngrammake --method=witten_bell domain.cnt domain.fst
ngrammerge --method=model_merge --alpha=0.1 --beta=0.9 generalp.fst domain.fst mix.fst
ngramprint --ARPA mix.fst > mix.arpa

# 2. Словарь: ru.dic, для слов вне его — G2P (2 варианта ударения) и ручные произношения лун
python3 - "$COMPILE/db/ru.dic" <<'PY'
import sys
want = set(open('words.list', encoding='utf-8').read().split()) - {'<unk>'}
lex, have = [], set()
for l in open(sys.argv[1], encoding='utf-8'):
    w = l.split(' ', 1)[0]
    if w in want: lex.append(l.rstrip('\n')); have.add(w)
open('lex-dic.txt', 'w', encoding='utf-8').write('\n'.join(lex) + '\n')
open('oov.txt', 'w', encoding='utf-8').write('\n'.join(sorted(want - have)) + '\n')
PY
phonetisaurus predict --nbest 2 --model "$COMPILE/db/ru-g2p/ru.fst" < oov.txt > lex-g2p.txt
rm -rf data graph && mkdir -p data/dict && cp "$COMPILE"/db/phone/* data/dict/
(echo "!SIL SIL"; echo "[unk] GBG"; cat lex-dic.txt lex-g2p.txt "$HERE/lex-hand.txt") | LC_ALL=C sort -u > data/dict/lexicon.txt
ln -sfn "$COMPILE/utils" utils; ln -sfn "$COMPILE/steps" steps
printf 'export PATH=%q\nexport LD_LIBRARY_PATH=%q\nexport LC_ALL=C\n' "$PATH" "$LD_LIBRARY_PATH" > path.sh   # его читают утилиты Kaldi
LC_ALL=C utils/prepare_lang.sh data/dict "[unk]" data/lang_local data/lang

# 3. Граф с lookahead (HCLr + Gr), как у маленьких моделей Vosk
# без ARPA скрипт строит только HCLr (Gr из G.fst падает — его делаем ниже сами)
LC_ALL=C utils/mkgraph_lookahead.sh --self-loop-scale 1.0 data/lang "$COMPILE/exp/chain/tdnn" graph || true
# слова в порядке lookahead-разметки; без <s> и </s> (у них один номер, OpenFst не читает такую таблицу)
utils/apply_map.pl --permissive -f 2 graph/relabel < data/lang/words.txt 2>/dev/null | grep -v '^</\?s> ' > graph/words-g.txt
ngramread --OOV_symbol="[unk]" --symbols=graph/words-g.txt --ARPA mix.arpa G.fst
fstarcsort --sort_type=ilabel G.fst | fstconvert --fst_type=ngram > graph/Gr.fst

# 4. Папка модели. Веса акустики округлены до 7 бит мантиссы (точность та же), final.mdl лежит в zip
#    (53 МБ вместо 118, одним файлом под лимит GitHub), сборка распаковывает
E=$COMPILE/exp/chain/extractor
rm -rf "$OUT" && mkdir -p "$OUT"/{am,conf,ivector,graph/phones}
nnet3-am-copy --binary=false "$COMPILE/exp/chain/tdnn/final.mdl" mdl.txt
python3 "$HERE/roundmdl.py" 7 mdl.txt mdl7.txt
mkdir -p am && nnet3-am-copy mdl7.txt am/final.mdl && rm mdl.txt mdl7.txt
(cd am && rm -f "$OUT/am/final.mdl.zip" && zip -q -9 "$OUT/am/final.mdl.zip" final.mdl)
cp "$HERE/mfcc.conf" "$HERE/model.conf" "$OUT/conf/"
cp "$E"/{final.dubm,final.ie,final.mat,global_cmvn.stats,online_cmvn.conf} "$OUT/ivector/"
tr ' ' '\n' < "$E/splice_opts" | grep . > "$OUT/ivector/splice.conf"
cp graph/HCLr.fst graph/Gr.fst graph/disambig_tid.int "$OUT/graph/"
cp graph/phones/word_boundary.int "$OUT/graph/phones/"
du -sh "$OUT"
