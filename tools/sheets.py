"""Preflight and code generation for the design sheets.

  python tools/sheets.py          preflight only: every cell filled, every cross-sheet reference resolves
  python tools/sheets.py --gen    preflight, then write src/Generated/Sheets.g.cs (one struct per row)

The build runs it with --gen; a dirty preflight fails the build.
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SHEETS = ROOT / "sheets"
SRC = ROOT / "src"
OUT = SRC / "Generated" / "Sheets.g.cs"

RO_INPUTS = {"jump", "skill1", "skill2", "skill3", "skill4", "sprint", "interact", "activateEquipment"}
SM64_FIELDS = {"buttonA", "buttonB", "buttonZ"}
SLOTS = ["passive", "primary", "secondary", "utility", "special"]


def load(name):
    return json.loads((SHEETS / f"{name}.json").read_text(encoding="utf-8"))


def classes_in_src():
    names = set()
    for f in SRC.rglob("*.cs"):
        if f.name.endswith(".g.cs"):
            continue
        names.update(re.findall(r"\b(?:class|struct)\s+([A-Za-z_][A-Za-z0-9_]*)", f.read_text(encoding="utf-8")))
    return names


def filled(v):
    if v is None:
        return False
    if isinstance(v, str):
        return v.strip() != ""
    if isinstance(v, list):
        return len(v) > 0 and all(filled(x) for x in v)
    return True


def preflight(sheets):
    problems = []
    for name, s in sheets.items():
        for i, row in enumerate(s["rows"]):
            for col in s["columns"]:
                if not filled(row.get(col)):
                    problems.append(f"{name}[{row.get('id', row.get('button', i))}].{col}: empty")
            extra = set(row) - set(s["columns"])
            if extra:
                problems.append(f"{name}[{i}]: cells outside the columns: {sorted(extra)}")

    skill_ids = {r["id"] for r in sheets["skills"]["rows"]}
    classes = classes_in_src()
    for r in sheets["attacks"]["rows"]:
        if r["skill"] not in skill_ids:
            problems.append(f"attacks[{r['id']}].skill: '{r['skill']}' is not a skills row")
        if not re.fullmatch(r"0x[0-9A-Fa-f]{8}", r["action"]):
            problems.append(f"attacks[{r['id']}].action: not an 8-digit hex action")
        if r["action"] == "0x00000000" and r["id"] != "stomp":
            problems.append(f"attacks[{r['id']}].action: only stomp has no SM64 action")
    actions = [r["action"] for r in sheets["attacks"]["rows"]]
    if len(actions) != len(set(actions)):
        problems.append("attacks.action: duplicate actions")
    for r in sheets["controls"]["rows"]:
        if r["field"] not in SM64_FIELDS:
            problems.append(f"controls[{r['button']}].field: '{r['field']}' is not an SM64MarioInputs button")
        for i in r["ror2Inputs"]:
            if i not in RO_INPUTS:
                problems.append(f"controls[{r['button']}].ror2Inputs: '{i}' is not an InputBankTest button")
    slots = [r["slot"] for r in sheets["skills"]["rows"]]
    if sorted(slots) != sorted(SLOTS):
        problems.append(f"skills.slot: need exactly one row per slot {SLOTS}, have {slots}")
    for r in sheets["skills"]["rows"]:
        if not (0 <= r["iconTile"] <= 10):
            problems.append(f"skills[{r['id']}].iconTile: Mario's texture has tiles 0-10")
        if not re.fullmatch(r"#[0-9A-Fa-f]{6}", r["iconColor"]):
            problems.append(f"skills[{r['id']}].iconColor: not #RRGGBB")
        if r["button"] not in {c["button"] for c in sheets["controls"]["rows"]}:
            problems.append(f"skills[{r['id']}].button: '{r['button']}' is not a controls row")
    for r in sheets["hooks"]["rows"]:
        if r["implementedIn"] not in classes:
            problems.append(f"hooks[{r['id']}].implementedIn: no class {r['implementedIn']} in src/")
    for r in sheets["tuning"]["rows"]:
        if r["usedBy"] not in classes:
            problems.append(f"tuning[{r['id']}].usedBy: no class {r['usedBy']} in src/")
    sv = sheets["survivor"]["rows"]
    if len(sv) != 1:
        problems.append("survivor: exactly one row")
    elif not re.fullmatch(r"#[0-9A-Fa-f]{6}", sv[0]["color"]):
        problems.append("survivor.color: not #RRGGBB")
    return problems


def cs(v):
    if isinstance(v, bool):
        return "true" if v else "false"
    if isinstance(v, (int, float)):
        return f"{float(v)!r}f"
    if isinstance(v, list):
        return "new[] { " + ", ".join(cs(x) for x in v) + " }"
    return json.dumps(v, ensure_ascii=False)


def pascal(s):
    return s[0].upper() + s[1:]


def gen_struct(name, columns, rows, types):
    fields = "\n".join(f"        public readonly {types[c]} {pascal(c)};" for c in columns)
    params = ", ".join(f"{types[c]} {c}" for c in columns)
    assigns = " ".join(f"{pascal(c)} = {c};" for c in columns)
    items = ",\n".join(
        "            new " + name + "(" + ", ".join(
            (f"0x{int(r[c], 16):08X}u" if types[c] == "uint" else cs(r[c])) if types[c] != "int" else str(int(r[c]))
            for c in columns) + ")"
        for r in rows)
    return f"""    internal readonly struct {name}
    {{
{fields}
        public {name}({params}) {{ {assigns} }}
    }}
""", items


def typ(v):
    if isinstance(v, bool):
        return "bool"
    if isinstance(v, (int, float)):
        return "float"
    if isinstance(v, list):
        return "string[]"
    return "string"


def generate(sheets):
    parts = ["// Generated by tools/sheets.py from sheets/*.json. Edit the sheets, not this file.",
             "namespace MarioRoR2", "{", "    internal static class Sheets", "    {"]
    body = []
    structs = []
    for name, struct in [("survivor", "SurvivorRow"), ("controls", "ControlRow"), ("skills", "SkillRow"),
                         ("attacks", "AttackRow"), ("hooks", "HookRow")]:
        s = sheets[name]
        types = {c: typ(s["rows"][0][c]) for c in s["columns"]}
        if name == "attacks":
            types["action"] = "uint"
        if name == "skills":
            types["iconTile"] = "int"
        st, items = gen_struct(struct, s["columns"], s["rows"], types)
        structs.append(st)
        prop = pascal(name)
        body.append(f"        public static readonly {struct}[] {prop} =\n        {{\n{items}\n        }};")
    for r in sheets["tuning"]["rows"]:
        body.append(f"        /// <summary>{r['note']} ({r['unit']})</summary>\n        public const float {pascal(r['id'])} = {cs(r['value'])};")
    nested = ["\n".join(("    " + l) if l else l for l in st.split("\n")) for st in structs]
    out = "\n".join(parts) + "\n" + "\n".join(body) + "\n\n" + "\n".join(nested) + "    }\n}\n"
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(out, encoding="utf-8")


def main():
    sheets = {n: load(n) for n in ["survivor", "controls", "skills", "attacks", "tuning", "hooks"]}
    problems = preflight(sheets)
    cells = sum(len(s["rows"]) * len(s["columns"]) for s in sheets.values())
    if problems:
        print(f"preflight: {len(problems)} problem(s) in {cells} cells")
        for p in problems:
            print("  - " + p)
        sys.exit(1)
    print(f"preflight: clean ({cells} cells, {sum(len(s['rows']) for s in sheets.values())} rows)")
    if "--gen" in sys.argv:
        generate(sheets)
        print(f"wrote {OUT.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
