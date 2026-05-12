#!/usr/bin/env python3
"""특허 Markdown → PDF 변환 스크립트.

사용법:
    python build_pdf.py <input.md> [output.pdf]
    python build_pdf.py build-model.md          # → build-model.pdf
    python build_pdf.py agent-mode.md           # → agent-mode.pdf

의존 패키지: fpdf2, Pillow
    pip install fpdf2 Pillow
"""

import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

from fpdf import FPDF
from PIL import Image as PILImage


class PatentPDF(FPDF):
    """특허 문서 전용 PDF 생성기."""

    def __init__(self):
        super().__init__()
        self.page_numbering_active = False
        self.page_number_offset = 0
        self.current_align = "L"

        # 한글 폰트 (시스템에서 탐색)
        regular_paths = [
            Path(r"C:\Windows\Fonts\NanumGothic.ttf"),
            Path(r"C:\Windows\Fonts\malgun.ttf"),
        ]
        bold_paths = [
            Path(r"C:\Windows\Fonts\NanumGothicBold.ttf"),
            Path(r"C:\Windows\Fonts\malgunbd.ttf"),
        ]
        mono_paths = [
            Path(r"C:\Windows\Fonts\NanumGothicCoding.ttf"),
            Path(r"C:\Windows\Fonts\D2Coding.ttf"),
        ]

        font_regular = next((p for p in regular_paths if p.exists()), None)
        font_bold = next((p for p in bold_paths if p.exists()), None)
        font_mono = next((p for p in mono_paths if p.exists()), None)

        if font_regular is None:
            print("경고: 한글 폰트를 찾을 수 없습니다. 기본 폰트를 사용합니다.")
            self.font_family_name = "Helvetica"
            self.mono_family_name = "Courier"
        else:
            self.add_font("Korean", "", str(font_regular))
            if font_bold:
                self.add_font("Korean", "B", str(font_bold))
            else:
                self.add_font("Korean", "B", str(font_regular))
            self.font_family_name = "Korean"
            # 코드블록용 모노스페이스 (한글 지원)
            if font_mono:
                self.add_font("KoreanMono", "", str(font_mono))
                self.mono_family_name = "KoreanMono"
            else:
                self.add_font("KoreanMono", "", str(font_regular))
                self.mono_family_name = "KoreanMono"

    def header(self):
        pass

    def footer(self):
        if self.page_numbering_active:
            self.set_y(-15)
            self.set_font(self.font_family_name, "", 9)
            page_num = self.page_no() - self.page_number_offset
            self.cell(0, 10, str(page_num), align="C")

    def chapter_title(self, title, level=1):
        sizes = {1: 16, 2: 14, 3: 12, 4: 11}
        size = sizes.get(level, 11)
        self.set_font(self.font_family_name, "B", size)
        self.ln(4 if level <= 2 else 2)
        self.multi_cell(0, size * 0.6, title, align=self.current_align)
        self.ln(2)

    def body_text(self, text):
        self.set_font(self.font_family_name, "", 10)
        # 일반 본문은 양쪽 정렬(justify). center/right 가 명시된 경우만 그대로 사용.
        align = "J" if self.current_align == "L" else self.current_align
        self.multi_cell(0, 6, text, align=align)
        self.ln(1)

    def code_block(self, text):
        self.set_font(self.mono_family_name, "", 8)
        self.set_fill_color(245, 245, 245)
        self.ln(1)
        for line in text.split("\n"):
            self.cell(0, 4.5, "  " + line, fill=True, new_x="LMARGIN", new_y="NEXT")
        self.ln(2)
        self.set_font(self.font_family_name, "", 10)

    def _measure_row_height(self, cells, header=False):
        """테이블 한 행의 렌더링 높이 계산 (폰트 side-effect 포함: set_font).

        table_row 의 실제 그리기와 build_pdf 의 pre-calc 모두 이 값을 사용하므로
        line_h / pad / font / col_w 로직을 한 곳에서 유지.
        """
        self.set_font(self.font_family_name, "B" if header else "", 9)
        col_count = len(cells) if cells else 1
        usable = self.w - self.l_margin - self.r_margin
        col_w = usable / col_count
        line_h, pad = 5.5, 1
        max_lines = 1
        for cell_text in cells:
            n = self._count_wrap_lines(cell_text.strip(), col_w - 2 * pad)
            if n > max_lines:
                max_lines = n
        return max_lines * line_h + 2 * pad

    def table_row(self, cells, header=False):
        row_h = self._measure_row_height(cells, header)  # set_font 도 함께 수행
        col_count = len(cells)
        usable = self.w - self.l_margin - self.r_margin
        col_w = usable / col_count
        line_h, pad = 5.5, 1

        x0 = self.l_margin
        y0 = self.get_y()

        # 페이지 넘김 체크
        if y0 + row_h > self.h - self.b_margin:
            self.add_page()
            y0 = self.get_y()

        # 셀 그리기
        for i, cell_text in enumerate(cells):
            x = x0 + i * col_w
            self.rect(x, y0, col_w, row_h)
            self.set_xy(x + pad, y0 + pad)
            self.multi_cell(col_w - 2 * pad, line_h, cell_text.strip(),
                            border=0, align="C" if header else "L")

        self.set_xy(x0, y0 + row_h)

    def _count_wrap_lines(self, text, width):
        """주어진 너비에서 텍스트가 몇 줄로 줄바꿈되는지 계산."""
        if not text or width <= 0:
            return 1
        words = text.split()
        lines = 1
        current = ""
        for word in words:
            test = f"{current} {word}".strip() if current else word
            if self.get_string_width(test) > width:
                if current:
                    lines += 1
                    current = word
                else:
                    lines += 1
                    current = ""
            else:
                current = test
        return max(1, lines)

    def add_nbsp(self):
        self.ln(8)


def _svg_dimensions(svg_path: Path) -> tuple:
    """SVG 파일에서 viewBox 또는 width/height를 읽어 (width, height) 반환."""
    tree = ET.parse(svg_path)
    root = tree.getroot()
    vb = root.get("viewBox")
    if vb:
        parts = vb.split()
        return float(parts[2]), float(parts[3])
    w = root.get("width", "100")
    h = root.get("height", "100")
    # pt/px 단위 제거
    w = float(re.sub(r"[^\d.]", "", w))
    h = float(re.sub(r"[^\d.]", "", h))
    return w, h


def _is_block_boundary(line: str) -> bool:
    """새 블록의 시작이 될 수 있는 줄인지 판별."""
    s = line.strip()
    return (not s
            or s.startswith("#")
            or s.startswith("```")
            or s.startswith("|")
            or s.startswith(">")
            or re.match(r"^(\s*)([-*]|\d+\.)\s+", line) is not None
            or "<!--" in line
            or s == "&nbsp;")


def parse_markdown(md_text: str) -> list:
    """마크다운을 (type, content) 튜플 리스트로 파싱."""
    tokens = []
    lines = md_text.split("\n")
    i = 0
    while i < len(lines):
        line = lines[i]

        # 커스텀 HTML 주석 지시자
        if "<!-- page-break -->" in line:
            tokens.append(("page-break", ""))
            i += 1
            continue
        if "<!-- page-number-reset -->" in line:
            tokens.append(("page-number-reset", ""))
            i += 1
            continue
        m = re.search(r"<!-- align:(center|left|right) -->", line)
        if m:
            tokens.append(("align", m.group(1)))
            i += 1
            continue

        # 이미지: ![alt](path)
        img_m = re.match(r"^!\[([^\]]*)\]\(([^)]+)\)", line.strip())
        if img_m:
            tokens.append(("image", img_m.group(2)))
            i += 1
            continue

        # 빈 줄
        if line.strip() == "":
            i += 1
            continue

        # &nbsp;
        if line.strip() == "&nbsp;":
            tokens.append(("nbsp", ""))
            i += 1
            continue

        # 코드 블록
        if line.strip().startswith("```"):
            code_lines = []
            i += 1
            while i < len(lines) and not lines[i].strip().startswith("```"):
                code_lines.append(lines[i])
                i += 1
            tokens.append(("code", "\n".join(code_lines)))
            i += 1
            continue

        # 헤딩
        hm = re.match(r"^(#{1,4})\s+(.*)", line)
        if hm:
            level = len(hm.group(1))
            tokens.append(("heading", (level, hm.group(2).strip())))
            i += 1
            continue

        # 테이블
        if "|" in line and i + 1 < len(lines) and re.match(r"^\|[\s\-:|]+\|", lines[i + 1]):
            rows = []
            header_cells = [c.strip() for c in line.strip().strip("|").split("|")]
            rows.append(("table-header", header_cells))
            i += 2  # 헤더 + 구분선 건너뛰기
            while i < len(lines) and "|" in lines[i] and lines[i].strip().startswith("|"):
                cells = [c.strip() for c in lines[i].strip().strip("|").split("|")]
                rows.append(("table-row", cells))
                i += 1
            tokens.append(("table", rows))
            continue

        # 리스트 항목
        lm = re.match(r"^(\s*)([-*]|\d+\.)\s+(.*)", line)
        if lm:
            indent = len(lm.group(1))
            content = lm.group(3).strip()
            # 연속 줄 수집
            i += 1
            while i < len(lines) and lines[i].strip() and not _is_block_boundary(lines[i]):
                content += " " + lines[i].strip()
                i += 1
            prefix = "  " * (indent // 2) + "- " if indent > 0 else "- "
            tokens.append(("text", prefix + content))
            continue

        # 블록인용 (blockquote: > ...)
        if line.strip().startswith(">"):
            bq_lines = []
            while i < len(lines) and lines[i].strip().startswith(">"):
                bq_lines.append(re.sub(r"^>\s?", "", lines[i].strip()))
                i += 1
            tokens.append(("text", " ".join(bq_lines)))
            continue

        # 일반 텍스트 (연속 줄 병합)
        text_lines = [line.strip()]
        i += 1
        while i < len(lines) and lines[i].strip() and not _is_block_boundary(lines[i]):
            text_lines.append(lines[i].strip())
            i += 1
        tokens.append(("text", " ".join(text_lines)))

    return tokens


def strip_md_formatting(text: str) -> str:
    """마크다운 인라인 포맷팅(볼드, 이탤릭, 코드) 제거."""
    text = re.sub(r"\*\*(.+?)\*\*", r"\1", text)
    text = re.sub(r"\*(.+?)\*", r"\1", text)
    text = re.sub(r"`(.+?)`", r"\1", text)
    return text


def build_pdf(md_path: str, out_path: str = None):
    md_text = Path(md_path).read_text(encoding="utf-8")
    if out_path is None:
        out_path = str(Path(md_path).with_suffix(".pdf"))

    tokens = parse_markdown(md_text)
    pdf = PatentPDF()
    pdf.set_auto_page_break(auto=True, margin=20)
    pdf.add_page()
    pdf.set_margins(25, 20, 25)

    md_dir = Path(md_path).parent

    for idx, (tok_type, content) in enumerate(tokens):
        if tok_type == "page-break":
            pdf.add_page()
        elif tok_type == "page-number-reset":
            pdf.page_numbering_active = True
            pdf.page_number_offset = pdf.page_no() - 1
        elif tok_type == "align":
            align_map = {"center": "C", "left": "L", "right": "R"}
            pdf.current_align = align_map.get(content, "L")
        elif tok_type == "nbsp":
            pdf.add_nbsp()
        elif tok_type == "heading":
            level, title = content
            # 다음 토큰이 image이면 heading+image 높이를 함께 체크하여 미리 page break
            if idx + 1 < len(tokens) and tokens[idx + 1][0] == "image":
                next_img_path = md_dir / tokens[idx + 1][1]
                if next_img_path.exists():
                    usable_w = pdf.w - pdf.l_margin - pdf.r_margin
                    if next_img_path.suffix.lower() == ".svg":
                        iw, ih = _svg_dimensions(next_img_path)
                    else:
                        with PILImage.open(next_img_path) as pil_img:
                            iw, ih = pil_img.size
                    img_h = usable_w * (ih / iw)
                    heading_h = 20  # heading + 여백 근사치
                    remaining = pdf.h - pdf.get_y() - pdf.b_margin
                    if img_h + heading_h > remaining:
                        pdf.add_page()
            pdf.chapter_title(strip_md_formatting(title), level)
        elif tok_type == "text":
            pdf.body_text(strip_md_formatting(content))
        elif tok_type == "code":
            pdf.code_block(content)
        elif tok_type == "table":
            # 테이블 전체 높이를 미리 계산하여, 한 페이지에 들어가는 크기면
            # keep-together 로 처리 (헤더만 남고 본문이 다음 페이지로 찢기는 현상 방지).
            rows = [(rt, [strip_md_formatting(c) for c in cells]) for rt, cells in content]
            total_h = sum(pdf._measure_row_height(cells, rt == "table-header") for rt, cells in rows)
            page_usable_h = pdf.h - pdf.t_margin - pdf.b_margin
            remaining = pdf.h - pdf.get_y() - pdf.b_margin
            if total_h > remaining and total_h <= page_usable_h:
                pdf.add_page()
            for rt, cells in rows:
                pdf.table_row(cells, header=(rt == "table-header"))
            pdf.ln(2)
        elif tok_type == "image":
            img_path = md_dir / content
            if not img_path.exists():
                print(f"경고: 도면 파일을 찾을 수 없습니다: {img_path}", file=sys.stderr)
            else:
                usable_w = pdf.w - pdf.l_margin - pdf.r_margin
                # 이미지 비율 계산
                if img_path.suffix.lower() == ".svg":
                    iw, ih = _svg_dimensions(img_path)
                else:
                    with PILImage.open(img_path) as pil_img:
                        iw, ih = pil_img.size
                img_h = usable_w * (ih / iw)
                remaining = pdf.h - pdf.get_y() - pdf.b_margin
                if img_h > remaining:
                    pdf.add_page()
                # auto page break 비활성화하여 이미지 중복 방지
                pdf.set_auto_page_break(auto=False)
                pdf.image(str(img_path), x=pdf.l_margin, w=usable_w)
                pdf.set_auto_page_break(auto=True, margin=20)
                pdf.ln(3)

    try:
        pdf.output(out_path)
    except PermissionError:
        print(f"\n*** {out_path} 가 다른 프로그램에서 열려 있습니다. PDF 뷰어를 닫고 다시 시도하세요. ***\n",
              file=sys.stderr)
        sys.exit(1)
    print(f"생성 완료: {out_path}")


if __name__ == "__main__":
    md_file = sys.argv[1] if len(sys.argv) > 1 else "build-model.md"
    out_file = sys.argv[2] if len(sys.argv) > 2 else None
    build_pdf(md_file, out_file)
