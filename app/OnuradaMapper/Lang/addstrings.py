"""Dev helper: adds missing keys to en.xaml / tr.xaml. usage: addstrings.py strings.json  ({"en": {...}, "tr": {...}})"""
import json, os, sys
from xml.sax.saxutils import escape

here = os.path.dirname(os.path.abspath(__file__))
data = json.load(open(sys.argv[1], encoding='utf-8'))
for lang, d in data.items():
    f = os.path.join(here, lang + '.xaml')
    s = open(f, encoding='utf-8').read()
    add = ''.join(f'    <s:String x:Key="{k}">{escape(v, {chr(34): "&quot;"})}</s:String>\n' for k, v in d.items() if f'x:Key="{k}"' not in s)
    s = s.replace('</ResourceDictionary>', add + '</ResourceDictionary>')
    open(f, 'w', encoding='utf-8').write(s)
    print(lang, add.count('\n'), 'added')
