from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED
import re

base = Path(r"C:\kwi-automations\projects\Schedule of events")
src = base / "Schedule of Events V8.7 - Codex Recalculated Template.xlsx"
out = base / "Schedule of Events V8.9 - Codex No Stale Formula Template.xlsx"
if out.exists():
    out.unlink()

with ZipFile(src, "r") as zin, ZipFile(out, "w", ZIP_DEFLATED) as zout:
    for item in zin.infolist():
        data = zin.read(item.filename)
        if item.filename == "xl/workbook.xml":
            text = data.decode("utf-8")
            m = re.search(r"<calcPr\b[^>]*/>", text)
            if not m:
                text = text.replace("</workbook>", '<calcPr calcId="191029" fullCalcOnLoad="0" calcOnSave="0"/></workbook>')
            else:
                calc = m.group(0)
                calc = re.sub(r'\s+calcMode="[^"]*"', "", calc)
                calc = re.sub(r'\s+forceFullCalc="[^"]*"', "", calc)
                if re.search(r'\sfullCalcOnLoad="[^"]*"', calc):
                    calc = re.sub(r'\sfullCalcOnLoad="[^"]*"', ' fullCalcOnLoad="0"', calc)
                else:
                    calc = calc.replace("/>", ' fullCalcOnLoad="0"/>')
                if re.search(r'\scalcOnSave="[^"]*"', calc):
                    calc = re.sub(r'\scalcOnSave="[^"]*"', ' calcOnSave="0"', calc)
                else:
                    calc = calc.replace("/>", ' calcOnSave="0"/>')
                text = text[:m.start()] + calc + text[m.end():]
            data = text.encode("utf-8")
        zout.writestr(item, data)

print(out)
