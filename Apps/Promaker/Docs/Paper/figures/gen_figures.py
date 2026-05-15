#!/usr/bin/env python3
"""논문 도면 생성 스크립트 (스켈레톤).

출력 경로: figures/paper/figNN.svg
도면 함수는 추후 추가. 스타일: 흑백, 박스+화살표.
"""

import os
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.patches import FancyBboxPatch

OUT_DIR = os.path.dirname(__file__) or "."
os.makedirs(OUT_DIR, exist_ok=True)

_font_found = False
for font_name in ["Malgun Gothic", "NanumGothic", "AppleGothic"]:
    try:
        matplotlib.rcParams["font.family"] = font_name
        matplotlib.rcParams["axes.unicode_minus"] = False
        fig_test = plt.figure(); fig_test.text(0.5, 0.5, "테스트"); plt.close(fig_test)
        _font_found = True
        break
    except Exception:
        continue
if not _font_found:
    print("경고: 한글 폰트를 찾을 수 없습니다. 도면 내 한글이 깨질 수 있습니다.", flush=True)

matplotlib.rcParams["svg.fonttype"] = "path"

# ─── 유틸리티 ───

def box(ax, x, y, w, h, text, fs=8, bold=False, fill="#f0f0f0", ec="black", lw=1.2):
    """박스를 그리고 (x,y,w,h) 반환. x,y는 중앙 좌표."""
    p = FancyBboxPatch((x-w/2, y-h/2), w, h, boxstyle="round,pad=0.05",
                        facecolor=fill, edgecolor=ec, linewidth=lw)
    ax.add_patch(p)
    ax.text(x, y, text, ha="center", va="center", fontsize=fs,
            fontweight="bold" if bold else "normal")
    return (x, y, w, h)

_PAD = 0.05  # FancyBboxPatch pad
def B(b): return b[1] - b[3]/2 - _PAD
def T(b): return b[1] + b[3]/2 + _PAD
def L(b): return b[0] - b[2]/2 - _PAD
def R(b): return b[0] + b[2]/2 + _PAD
def CX(b): return b[0]
def CY(b): return b[1]

def arr(ax, x1, y1, x2, y2, text="", fs=7, style="->", color="black"):
    ax.annotate("", xy=(x2, y2), xytext=(x1, y1),
                arrowprops=dict(arrowstyle=style, color=color, lw=1.1))
    if text:
        mx, my = (x1+x2)/2, (y1+y2)/2
        dx = abs(x2-x1); dy = abs(y2-y1)
        if dy > dx:
            ax.text(mx+0.12, my, text, ha="left", va="center", fontsize=fs, color=color)
        else:
            ax.text(mx, my+0.08, text, ha="center", va="bottom", fontsize=fs, color=color)

def varr(ax, b1, b2, text=""):
    """b1 하단 → b2 상단 수직 화살표."""
    arr(ax, CX(b1), B(b1), CX(b2), T(b2), text)

def harr(ax, b1, b2, text=""):
    """b1 우측 → b2 좌측 수평 화살표."""
    arr(ax, R(b1), CY(b1), L(b2), CY(b2), text)

def fan_x(b, n, i):
    """박스 가로 변 위에 n개 중 i번째 위치 (균등 분산)."""
    l, r = L(b) + 0.08, R(b) - 0.08
    if n == 1:
        return CX(b)
    return l + (r - l) * i / (n - 1)

def new_fig(w=10, h=7):
    fig, ax = plt.subplots(1, 1, figsize=(w, h))
    ax.set_xlim(0, w); ax.set_ylim(0, h)
    ax.set_aspect("equal"); ax.axis("off")
    return fig, ax

def save(fig, name):
    path = os.path.join(OUT_DIR, name)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    fig.savefig(path, format="svg", bbox_inches="tight", facecolor="white")
    plt.close(fig)
    # fpdf2 SVG 렌더러가 기본 fill:black 을 적용하지 않으므로,
    # 모든 text 그룹에 명시적 fill 추가 (개별 색상이 있는 텍스트는 내부 <g>가 override)
    with open(path, "r", encoding="utf-8") as f:
        svg = f.read()
    svg = svg.replace('<g id="text_', '<g style="fill: #000000" id="text_')
    with open(path, "w", encoding="utf-8") as f:
        f.write(svg)
    print(f"  {name}")


# ═══════════════════════════════════════════════════════════════
# 도면 함수 (paper/figNN.svg)
# ═══════════════════════════════════════════════════════════════
# 예시:
#   def fig01():
#       fig, ax = new_fig(10, 7)
#       b1 = box(ax, 5, 5, 2, 1, "블록 A")
#       b2 = box(ax, 5, 2, 2, 1, "블록 B")
#       varr(ax, b1, b2)
#       save(fig, "paper/fig01.svg")


if __name__ == "__main__":
    # 추가된 도면 함수를 여기서 호출
    pass
