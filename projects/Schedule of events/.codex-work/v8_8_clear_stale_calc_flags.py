from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED
import shutil
import tempfile
import xml.etree.ElementTree as ET

base = Path(r"C:\kwi-automations\projects\Schedule of events")
src = base / "Schedule of Events V8.7 - Codex Recalculated Template.xlsx"
out = base / "Schedule of Events V8.8 - Codex No Stale Formula Template.xlsx"

if out.exists():
    out.unlink()

ns = {"main": "http://schemas.openxmlformats.org/spreadsheetml/2006/main"}
ET.register_namespace("", ns["main"])

with tempfile.TemporaryDirectory() as td:
    td = Path(td)
    with ZipFile(src, "r") as zin:
        zin.extractall(td)

    workbook_xml = td / "xl" / "workbook.xml"
    tree = ET.parse(workbook_xml)
    root = tree.getroot()
    calc_pr = root.find("main:calcPr", ns)
    if calc_pr is None:
        calc_pr = ET.SubElement(root, f"{{{ns['main']}}}calcPr")
    calc_pr.attrib.pop("calcMode", None)
    calc_pr.attrib.pop("forceFullCalc", None)
    calc_pr.attrib["fullCalcOnLoad"] = "0"
    calc_pr.attrib["calcOnSave"] = "0"
    tree.write(workbook_xml, encoding="utf-8", xml_declaration=True)

    with ZipFile(out, "w", ZIP_DEFLATED) as zout:
        for path in td.rglob("*"):
            if path.is_file():
                zout.write(path, path.relative_to(td).as_posix())

print(out)
