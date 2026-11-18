"""Run a scripted play session and collect what went wrong.

Usage:
    python Utils/Playtest/playtest.py scenarios/smoke.lua [--map gm_construct] [--config Release] [--rounds 10]

The scenario is injected as a Lua autorun file, the game is launched, its console
output is recorded, and the session is stopped once the scenario reports it is
done (or the timeout expires). When the game crashes, the item that was being
exercised is skipped and the scenario is run again, up to --rounds times.
A report with grouped exceptions, Lua errors and screenshots is written to
Utils/Playtest/runs/<timestamp>-<scenario>-<map>/.
"""

import argparse
import collections
import datetime
import os
import re
import subprocess
import sys
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.environ.get("PLAYTEST_REPO") or os.path.join(HERE, "..", ".."))
AUTORUN_NAME = "zz_playtest.lua"

EXCEPTION_RE = re.compile(r"\b((?:[A-Za-z_][\w`]*\.)*[A-Za-z_]\w*Exception)\b(?::\s*(.*))?")
STACK_RE = re.compile(r"^\s+at (.+?)(?: in (.+?):line (\d+))?\s*$")
LUA_ERROR_RE = re.compile(r"^\[(.+?)\] (.+)$")
LUA_STACK_RE = re.compile(r"^\s+\d+\. .+ - .+:-?\d+\s*$")
ANSI_RE = re.compile("\x1b" + r"\[[0-9;]*[A-Za-z]")
MARK_RE = re.compile(r"##PT_(\w+)## \[(\w+)\] ?(.*?)\s*$")
UNKNOWN_ENTITY_RE = re.compile(r"Attempted to create unknown entity type (\S+?)!")


def parse_args():
    p = argparse.ArgumentParser()
    p.add_argument("scenario", help="Lua scenario file (relative to this folder or absolute)")
    p.add_argument("--map", default="gm_construct")
    p.add_argument("--config", default="Release")
    p.add_argument("--timeout", type=float, default=300)
    p.add_argument("--rounds", type=int, default=1, help="rerun after a crash, skipping the item that caused it")
    p.add_argument("--width", type=int, default=1280)
    p.add_argument("--height", type=int, default=720)
    p.add_argument("--keep-open", action="store_true", help="do not stop the game when the scenario is done")
    p.add_argument("--extra", default="", help="extra command line arguments for the game")
    return p.parse_args()


def resolve(path):
    if os.path.isabs(path) and os.path.exists(path):
        return path
    for base in (os.getcwd(), HERE):
        candidate = os.path.join(base, path)
        if os.path.exists(candidate):
            return os.path.abspath(candidate)
    sys.exit(f"scenario not found: {path}")


def kill_tree(pid):
    subprocess.run(["taskkill", "/T", "/F", "/PID", str(pid)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def lua_string(text):
    return '"' + text.replace("\\", "\\\\").replace('"', '\\"') + '"'


def install_scenario(game_dir, scenario_path, skip):
    with open(os.path.join(HERE, "playtest_lib.lua"), encoding="utf-8") as f:
        lib = f.read()
    with open(scenario_path, encoding="utf-8") as f:
        scenario = f.read()
    skip_lua = "PT.Skip = {" + ", ".join(f"[{lua_string(s)}] = true" for s in sorted(skip)) + "}\n"
    source = lib + "\n" + skip_lua + "\n-- scenario: " + os.path.basename(scenario_path) + "\n" + scenario + "\n"

    installed = []
    for realm in ("server", "client"):
        folder = os.path.join(game_dir, "lua", "autorun", realm)
        os.makedirs(folder, exist_ok=True)
        target = os.path.join(folder, AUTORUN_NAME)
        with open(target, "w", encoding="utf-8", newline="\n") as f:
            f.write(source)
        installed.append(target)
    return installed


def clear_old_shots(shot_dir):
    if not os.path.isdir(shot_dir):
        return
    for name in os.listdir(shot_dir):
        if name.startswith("pt_"):
            os.remove(os.path.join(shot_dir, name))


def run_game(args, build_dir, log_path):
    exe = os.path.join(build_dir, "Source.Launcher.exe")
    if not os.path.exists(exe):
        sys.exit(f"game not built: {exe}")

    cmd = [exe, "-windowed", "-w", str(args.width), "-h", str(args.height),
           "-noworkshop", "-noaddons", "+sv_cheats", "1", "+map", args.map]
    if args.extra:
        cmd += args.extra.split()

    marks = []
    start = time.time()
    with open(log_path, "w", encoding="utf-8", errors="replace") as log:
        proc = subprocess.Popen(cmd, cwd=build_dir, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                stdin=subprocess.DEVNULL, bufsize=0)
        done = threading.Event()

        def reader():
            for raw in iter(proc.stdout.readline, b""):
                line = ANSI_RE.sub("", raw.decode("utf-8", errors="replace")).rstrip("\r\n")
                log.write(line + "\n")
                log.flush()
                m = MARK_RE.search(line)
                if m:
                    kind, realm, text = m.groups()
                    marks.append((time.time() - start, kind, realm, text))
                    if kind not in ("ITEM",):
                        print(f"  [{time.time() - start:6.1f}s] {kind:5} {realm} {text[:150]}")
                    if kind == "DONE" and realm == "cl":
                        done.set()
            done.set()

        t = threading.Thread(target=reader, daemon=True)
        t.start()

        outcome = "timeout"
        while time.time() - start < args.timeout:
            if done.wait(0.5):
                outcome = "done" if any(k == "DONE" and r == "cl" for _, k, r, _ in marks) else "exited"
                break

        if outcome == "done":
            time.sleep(1.5)  # let the last screenshots be written
        exit_code = proc.poll()
        if exit_code is not None and outcome != "done":
            outcome = "crashed" if exit_code != 0 else "exited"

        if not (args.keep_open and outcome == "done"):
            if proc.poll() is None:
                kill_tree(proc.pid)
        t.join(timeout=5)

    return outcome, exit_code, marks, time.time() - start


def parse_log(log_path):
    with open(log_path, encoding="utf-8", errors="replace") as f:
        lines = f.read().splitlines()

    exceptions = collections.OrderedDict()
    lua_errors = collections.OrderedDict()
    unknown_entities = collections.Counter()
    warnings = collections.Counter()

    i = 0
    while i < len(lines):
        line = lines[i]

        m = UNKNOWN_ENTITY_RE.search(line)
        if m:
            unknown_entities[m.group(1)] += 1
            i += 1
            continue

        m = EXCEPTION_RE.search(line)
        if m and "##PT_" not in line:
            frames = []
            j = i + 1
            while j < len(lines) and STACK_RE.match(lines[j]):
                frames.append(STACK_RE.match(lines[j]))
                j += 1
            if frames:
                ex_type = m.group(1)
                message = (m.group(2) or "").strip()
                top = frames[0].group(1)
                where = f"{os.path.basename(frames[0].group(2))}:{frames[0].group(3)}" if frames[0].group(2) else ""
                key = (ex_type, top)
                entry = exceptions.setdefault(key, {"type": ex_type, "message": message, "top": top, "where": where,
                                                    "count": 0, "first_line": i + 1,
                                                    "fatal": "Unhandled exception" in line,
                                                    "stack": [f.group(0).strip() for f in frames[:10]]})
                entry["count"] += 1
                i = j
                continue

        m = LUA_ERROR_RE.match(line)
        if m and i + 1 < len(lines) and LUA_STACK_RE.match(lines[i + 1]):
            stack = []
            j = i + 1
            while j < len(lines) and LUA_STACK_RE.match(lines[j]):
                stack.append(lines[j].strip())
                j += 1
            key = m.group(2)
            entry = lua_errors.setdefault(key, {"tag": m.group(1), "message": m.group(2), "count": 0,
                                                "first_line": i + 1, "stack": stack[:6]})
            entry["count"] += 1
            i = j
            continue

        text = re.sub(r"^\[\w+\] ", "", line.strip())
        low = text.lower()
        if "##pt_" not in low and low.startswith(("warning", "couldn't", "could not", "failed", "unable", "missing", "error", "bad ")):
            warnings[re.sub(r"\d+", "#", text)[:160]] += 1
        i += 1

    return exceptions, lua_errors, unknown_entities, warnings


def collect_shots(shot_dir, out_dir, marks, prefix):
    try:
        from PIL import Image, ImageStat
    except ImportError:
        return []

    results = []
    for _, kind, _, name in marks:
        if kind != "SHOT":
            continue
        best = None
        for suffix in "abc":
            path = os.path.join(shot_dir, f"pt_{name}_{suffix}.tga")
            if not os.path.exists(path):
                continue
            img = Image.open(path).convert("RGB")
            brightness = sum(ImageStat.Stat(img).mean) / 3
            if best is None or brightness > best[0]:
                best = (brightness, img)
        if best is None:
            results.append((name, None, 0.0))
            continue
        png = os.path.join(out_dir, f"{prefix}{name}.png")
        best[1].save(png)
        results.append((name, png, best[0]))
    clear_old_shots(shot_dir)
    return results


def last_item(marks):
    for _, kind, _, text in reversed(marks):
        if kind == "ITEM":
            return text
    return None


FRAME_TYPE_RE = re.compile(r"^at (?:[\w`]+\.)*?(\w+)\.[\w<>|`]+\(")


def normalize(name):
    return name.replace("_", "").lower()


def crash_culprits(marks, fatal_stack):
    """Items whose entity class appears in the crash stack, or the last item as a fallback."""
    item_classes = collections.defaultdict(set)
    current = None
    for _, kind, _, text in marks:
        if kind == "ITEM":
            current = text
        elif kind == "ICLASS" and current:
            item_classes[normalize(text)].add(current)

    for frame in fatal_stack:
        m = FRAME_TYPE_RE.match(frame)
        if m and normalize(m.group(1)) in item_classes:
            return sorted(item_classes[normalize(m.group(1))]), m.group(1)

    item = last_item(marks)
    return ([item] if item else []), None


def merge(into, entries, round_index):
    for key, e in entries.items():
        if key in into:
            into[key]["count"] += e["count"]
        else:
            e = dict(e)
            e["round"] = round_index
            into[key] = e


def write_report(out_dir, args, rounds, exceptions, lua_errors, unknown_entities, warnings, shots):
    lines = [f"# Playtest {os.path.basename(args.scenario)} on {args.map}", ""]

    lines += ["## Rounds", ""]
    for r in rounds:
        line = f"- Round {r['index']}: **{r['outcome']}** after {r['duration']:.0f}s"
        if r["crash_item"]:
            line += f", crashed while running `{r['crash_item']}` (skipped next round)"
        lines.append(line)
    lines.append("")

    fatal = [e for e in exceptions.values() if e["fatal"]]
    handled = [e for e in exceptions.values() if not e["fatal"]]
    item_fails = [(r["index"], text) for r in rounds for text in r["item_fails"]]
    step_fails = [(r["index"], text) for r in rounds for text in r["step_fails"]]

    lines += [
        "## Summary", "",
        f"- Crashes (unhandled exceptions): {len(fatal)}",
        f"- Other C# exceptions: {len(handled)} distinct",
        f"- Lua errors: {len(lua_errors)} distinct",
        f"- Unknown entity classes: {len(unknown_entities)}",
        f"- Failed items: {len(item_fails)}, failed steps: {len(step_fails)}",
        "",
    ]

    def exception_block(e):
        out = [f"### {e['type']} at `{e['top']}` x{e['count']}"]
        if e["message"]:
            out.append(e["message"])
        out += ["```"] + e["stack"] + ["```", ""]
        return out

    if fatal:
        lines += ["## Crashes", ""]
        for e in fatal:
            lines += exception_block(e)

    if handled:
        lines += ["## Other C# exceptions", ""]
        for e in sorted(handled, key=lambda e: -e["count"]):
            lines += exception_block(e)

    if lua_errors:
        lines += ["## Lua errors", ""]
        for e in sorted(lua_errors.values(), key=lambda e: -e["count"]):
            lines.append(f"### [{e['tag']}] {e['message']} x{e['count']}")
            if e["stack"]:
                lines += ["```"] + e["stack"] + ["```"]
            lines.append("")

    if item_fails or step_fails:
        lines += ["## Failed items and steps", ""]
        for idx, text in step_fails + item_fails:
            lines.append(f"- (round {idx}) {text.splitlines()[0][:300]}")
        lines.append("")

    if unknown_entities:
        lines += ["## Unknown entity classes", "", ", ".join(f"`{name}`" for name in sorted(unknown_entities)), ""]

    if shots:
        lines += ["## Screenshots", ""]
        for name, png, brightness in shots:
            if png is None:
                lines.append(f"- {name}: missing")
            else:
                flag = " (black)" if brightness < 2 else ""
                lines.append(f"- [{name}]({os.path.basename(png)}) brightness {brightness:.0f}{flag}")
        lines.append("")

    if warnings:
        lines += ["## Most frequent warnings", ""]
        for text, count in warnings.most_common(30):
            lines.append(f"- x{count} `{text}`")
        lines.append("")

    report = os.path.join(out_dir, "report.md")
    with open(report, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))
    return report


def main():
    args = parse_args()
    scenario_path = resolve(args.scenario)
    build_dir = os.path.join(REPO, "Build", args.config)
    game_dir = os.path.join(build_dir, "sdn")
    shot_dir = os.path.join(game_dir, "screenshots")

    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    out_dir = os.path.join(HERE, "runs", f"{stamp}-{os.path.splitext(os.path.basename(scenario_path))[0]}-{args.map}")
    os.makedirs(out_dir, exist_ok=True)

    skip = set()
    rounds = []
    exceptions = collections.OrderedDict()
    lua_errors = collections.OrderedDict()
    unknown_entities = collections.Counter()
    warnings = collections.Counter()
    shots = []

    for index in range(1, args.rounds + 1):
        log_path = os.path.join(out_dir, f"console_{index}.log")
        clear_old_shots(shot_dir)
        installed = install_scenario(game_dir, scenario_path, skip)
        print(f"Round {index}: {os.path.basename(scenario_path)} on {args.map} ({args.config}), {len(skip)} skipped")
        try:
            outcome, exit_code, marks, duration = run_game(args, build_dir, log_path)
        finally:
            for path in installed:
                if os.path.exists(path):
                    os.remove(path)

        ex, lua, unknown, warn = parse_log(log_path)
        merge(exceptions, ex, index)
        merge(lua_errors, lua, index)
        unknown_entities.update(unknown)
        warnings.update(warn)
        shots += collect_shots(shot_dir, out_dir, marks, f"r{index}_")

        culprits, culprit_type = [], None
        if outcome in ("crashed", "exited", "timeout"):
            fatal_stack = next((e["stack"] for e in ex.values() if e["fatal"]), [])
            culprits, culprit_type = crash_culprits(marks, fatal_stack)
        crash_item = ", ".join(culprits) + (f" (class {culprit_type} in stack)" if culprit_type else " (last item)") if culprits else None
        rounds.append({
            "index": index, "outcome": outcome, "exit_code": exit_code, "duration": duration,
            "crash_item": crash_item,
            "item_fails": [text for _, k, _, text in marks if k == "IFAIL"],
            "step_fails": [text for _, k, _, text in marks if k == "FAIL"],
        })
        print(f"  -> {outcome}" + (f", skipping {crash_item}" if crash_item else ""))

        new = [c for c in culprits if c not in skip]
        if outcome == "done" or not new:
            break
        skip.update(new)

    report = write_report(out_dir, args, rounds, exceptions, lua_errors, unknown_entities, warnings, shots)
    fatal = sum(1 for e in exceptions.values() if e["fatal"])
    print(f"{len(rounds)} round(s), {fatal} crash(es), {len(exceptions) - fatal} other C# exceptions, {len(lua_errors)} Lua errors")
    print(f"Report: {report}")
    return 0 if rounds[-1]["outcome"] == "done" and not exceptions and not lua_errors else 1


if __name__ == "__main__":
    sys.exit(main())
