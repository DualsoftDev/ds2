#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
build-fonts.py - DSPilot web font subset builder.

Why: wwwroot/fonts shipped raw TTF (NotoSansKR 6.1 MB x4) and the full variable
Material Symbols (3.9 MB). Every page links css/fonts.css, so a cold visit pulled
~22 MB of fonts and starved the HTTP/1.1 connection pool for ~20 s on a remote
(Tailscale) link. This script produces woff2 subsets that keep every glyph the UI
can actually render, and splits the rarely used CJK ideographs into a second face
that a browser downloads only if such a character is painted.

Inputs  : tools/fonts-src/*.ttf|woff2 (originals, kept out of the published app)
Outputs : DSPilot/wwwroot/fonts/*.woff2  (+ a size report on stdout)

Run: python tools/build-fonts.py      (needs: pip install fonttools brotli)
ASCII-only output on purpose - see reference_windows_bat_crlf_utf8_chcp_misparse.
"""
import os
import sys

from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.varLib import instancer

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "fonts-src")
OUT = os.path.join(HERE, "..", "DSPilot", "wwwroot", "fonts")

# Latin, punctuation, currency, arrows/math/geometry/misc symbols, CJK punctuation,
# Hangul jamo + compatibility jamo + all 11172 precomposed syllables, halfwidth/fullwidth.
# Covers any Korean string the UI can produce (AASX names, user tags, alarm text).
PRIMARY = [
    (0x0000, 0x024F), (0x0250, 0x02FF), (0x0300, 0x036F), (0x0370, 0x03FF),
    (0x0400, 0x04FF), (0x1E00, 0x1EFF),
    (0x2000, 0x206F), (0x2070, 0x209F), (0x20A0, 0x20BF), (0x2100, 0x214F),
    (0x2150, 0x218F), (0x2190, 0x21FF), (0x2200, 0x22FF), (0x2300, 0x23FF),
    (0x2460, 0x24FF), (0x2500, 0x257F), (0x25A0, 0x25FF), (0x2600, 0x27BF),
    (0x2B00, 0x2BFF), (0x3000, 0x303F), (0x1100, 0x11FF), (0x3130, 0x318F),
    (0xA960, 0xA97F), (0xAC00, 0xD7A3), (0xD7B0, 0xD7FF), (0xFF00, 0xFFEF),
    (0xFE0E, 0xFE0F),
]

# Everything the source font has outside PRIMARY: Hanja, kana, and stray symbols.
# Declared as a separate @font-face so it is fetched only when such a glyph is drawn.
EXTRA_CSS_RANGE = ("U+2E80-2FDF, U+3040-30FF, U+3190-319F, U+31F0-31FF, "
                   "U+3200-4DBF, U+4E00-9FFF, U+A000-A4CF, U+F900-FAFF, U+FE30-FE4F")

NOTO = [("NotoSansKR-Regular.ttf", 400), ("NotoSansKR-Medium.ttf", 500),
        ("NotoSansKR-Bold.ttf", 700)]


def _opts():
    o = subset.Options()
    o.flavor = "woff2"
    o.hinting = False
    o.desubroutinize = False
    o.notdef_outline = True
    o.drop_tables += ["DSIG"]
    o.layout_features = ["ccmp", "liga", "locl", "kern", "mark", "mkmk", "calt", "rlig"]
    return o


def _write(font, unicodes, out_path):
    s = subset.Subsetter(options=_opts())
    s.populate(unicodes=unicodes)
    s.subset(font)
    font.flavor = "woff2"
    font.save(out_path)
    return os.path.getsize(out_path)


def build_noto():
    rows = []
    for name, weight in NOTO:
        src = os.path.join(SRC, name)
        cmap = set(TTFont(src, lazy=True).getBestCmap().keys())
        primary = sorted(c for c in cmap
                         if any(lo <= c <= hi for lo, hi in PRIMARY))
        extra = sorted(cmap - set(primary))
        base = name.replace(".ttf", "")
        n1 = _write(TTFont(src), primary, os.path.join(OUT, base + ".kr.woff2"))
        rows.append((base + ".kr.woff2", weight, len(primary), n1))
        # One CJK-ideograph fallback face is enough; browsers reuse it across weights.
        if weight == 400:
            n2 = _write(TTFont(src), extra, os.path.join(OUT, base + ".cjk.woff2"))
            rows.append((base + ".cjk.woff2", weight, len(extra), n2))
    return rows


def build_symbols():
    """Flatten the variable axes to the only instance the CSS ever asks for
    (FILL 0 / wght 400 / GRAD 0 / opsz 24). Every icon glyph is kept, so icon
    names built at runtime keep working - see reference_dspilot_material_icons_classic_set."""
    src = os.path.join(SRC, "MaterialSymbolsOutlined.woff2")
    f = TTFont(src)
    axes = {a.axisTag: a.defaultValue for a in f["fvar"].axes}
    pin = {"FILL": 0, "wght": 400, "GRAD": 0, "opsz": 24}
    f = instancer.instantiateVariableFont(f, {k: v for k, v in pin.items() if k in axes})
    out = os.path.join(OUT, "MaterialSymbolsOutlined.static.woff2")
    f.flavor = "woff2"
    f.save(out)
    return [("MaterialSymbolsOutlined.static.woff2", 400,
             len(f.getGlyphOrder()), os.path.getsize(out))]


def css_ranges(pairs):
    return ", ".join("U+%04X" % lo if lo == hi else "U+%04X-%04X" % (lo, hi)
                     for lo, hi in pairs)


def main():
    if not os.path.isdir(SRC):
        sys.exit("missing source folder: " + SRC)
    rows = build_noto() + build_symbols()
    before = sum(os.path.getsize(os.path.join(SRC, n)) for n in os.listdir(SRC))
    after = sum(r[3] for r in rows)
    print("%-42s %6s %8s %10s" % ("file", "weight", "glyphs", "KB"))
    for n, w, g, b in rows:
        print("%-42s %6d %8d %10.0f" % (n, w, g, b / 1024.0))
    print("-" * 70)
    print("sources %.1f MB -> built %.1f MB" % (before / 1048576.0, after / 1048576.0))
    print("")
    print("css/fonts.css must use these unicode-range values verbatim:")
    print("  .kr  : " + css_ranges(PRIMARY))
    print("  .cjk : " + EXTRA_CSS_RANGE)


if __name__ == "__main__":
    main()
