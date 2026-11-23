"""Create Chinese developer-release guides; requires ReportLab, pypdf and Poppler."""
from pathlib import Path
import re
import html
from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer
from reportlab.lib.styles import ParagraphStyle
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.lib import colors
from reportlab.lib.pagesizes import A4
from pypdf import PdfReader
import subprocess
import shutil

root = Path(__file__).resolve().parent.parent
release = root / "artifacts" / "Release"
preview = root / "artifacts" / "validation" / "phase6" / "manuals"
release.mkdir(parents=True, exist_ok=True)
preview.mkdir(parents=True, exist_ok=True)
pdfmetrics.registerFont(TTFont("GuideCN", r"C:\Windows\Fonts\msyh.ttc", subfontIndex=0))
body = ParagraphStyle("Body", fontName="GuideCN", fontSize=10, leading=17, spaceAfter=9, wordWrap="CJK", textColor=colors.HexColor("#263442"))
title = ParagraphStyle("Title", parent=body, fontSize=19, leading=29, textColor=colors.HexColor("#174878"), spaceAfter=18)
heading = ParagraphStyle("Heading", parent=body, fontSize=13, leading=20, textColor=colors.HexColor("#174878"), spaceBefore=11, spaceAfter=9, keepWithNext=True)

def footer(canvas, doc):
    canvas.setFont("GuideCN", 8)
    canvas.setFillColor(colors.HexColor("#647486"))
    canvas.drawString(42, 25, "CloudDrive Lite 0.6.0 | 开发包 | 2026-11-20")
    canvas.drawRightString(A4[0] - 42, 25, str(doc.page))

for source, name in [("INSTALL_GUIDE.md", "InstallGuide"), ("RECOVERY_GUIDE.md", "RecoveryGuide")]:
    text = (root / "docs" / source).read_text(encoding="utf-8-sig")
    flow = []
    for block in re.split(r"\n\s*\n", text.strip()):
        if block.startswith("# "):
            style, value = title, block[2:]
        elif block.startswith("## "):
            style, value = heading, block[3:]
        else:
            style, value = body, block
        flow.append(Paragraph(html.escape(value).replace("\n", "<br/>"), style))
    output = release / (name + ".pdf")
    SimpleDocTemplate(str(output), pagesize=A4, leftMargin=42, rightMargin=42, topMargin=38, bottomMargin=44, title="CloudDrive Lite " + name, author="CloudDrive Lite").build(flow, onFirstPage=footer, onLaterPages=footer)
    pdf = PdfReader(output)
    extracted = "".join(page.extract_text() for page in pdf.pages)
    assert "开发包" in extracted and "恢复" in extracted and len(extracted) > 500
    renderer = shutil.which("pdftoppm")
    if not renderer:
        raise RuntimeError("Poppler pdftoppm is required for visual QA")
    subprocess.run([renderer, "-r", "96", "-png", str(output), str(preview / name)], check=True)
    print(f"PASS {output}: {len(pdf.pages)} pages; text extracted; every page rendered")
