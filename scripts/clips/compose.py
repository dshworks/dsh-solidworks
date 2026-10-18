"""Compose a recorded session into a clip: the real SOLIDWORKS window, captioned with the real
dsh session that drove it.

  compose.py <session dir> <out stem> [--final "verbatim line" ...] [--width 1024]

Every caption comes from the session's own events: the prompt as typed, each tool call with a
summary of its real arguments and result, and closing lines that must be verbatim substrings of
the model's final answer (checked here; the script refuses otherwise). Time is warped, not
edited: idle stretches while the model thinks are fast-forwarded, stretches where the window
changes play near real time. Nothing is reordered or invented.
"""
import json, os, subprocess, sys, textwrap
import numpy as np
from PIL import Image, ImageDraw, ImageFont

args = sys.argv[1:]
session, stem = args[0], args[1]
finals, width = [], 1024
i = 2
while i < len(args):
    if args[i] == "--final": finals.append(args[i + 1]); i += 2
    elif args[i] == "--width": width = int(args[i + 1]); i += 2
    else: raise SystemExit(f"unknown argument {args[i]}")

off = json.load(open(f"{session}/clock.json"))["vm_minus_local"]
events = [json.loads(l) for l in open(f"{session}/events.jsonl")]
import re
plain = lambda s: re.sub(r"\s+", " ", s.replace("**", "").replace("|", " ").replace("`", "")).strip()
final_text = plain(next((e["text"] for e in events if e.get("type") == "final"), ""))
finals = [plain(f) for f in finals]
for f in finals:  # markdown aside, every closing line is the model's own words
    if f not in final_text: raise SystemExit(f"--final line is not in the model's answer: {f!r}")
prompt = next(e for e in events if e.get("type") == "prompt")
t_start, t_end = prompt["t"], next(e["t"] for e in events if e.get("type") == "final")
frames = sorted((int(f[:-4]) - off, f) for f in os.listdir(session) if f.endswith(".jpg"))
frames = [x for x in frames if t_start - 500 <= x[0] <= t_end + 1500]

# --- captions from events -------------------------------------------------------------
def num(v): return f"{v:,.1f}" if isinstance(v, (int, float)) else str(v)

def call_text(tool, a):
    if tool == "sw_build":
        parts = []
        for s in a.get("steps", []):
            op = s.get("op")
            if op == "sketch": parts.append("sketch " + ("on face" if "face_at" in s else f"on {s.get('on', 'front')}"))
            elif op == "extrude": parts.append("cut" if s.get("cut") else "extrude")
            else: parts.append(op)
        return "sw_build  " + ", ".join(parts)
    if tool == "sw_view": return "sw_view  " + ", ".join(a.get("views") or [a.get("view", "isometric")])
    if tool == "sw_probe": return f"sw_probe  {len(a.get('at', []))} points" if a.get("at") else "sw_probe  the user's selection"
    if tool == "sw_new": return f"sw_new  {a.get('type', 'part')}"
    if tool == "sw_assemble": return "sw_assemble  " + ", ".join(s.get("type", s.get("op")) if s.get("op") == "mate" else s.get("op") for s in a.get("steps", []))
    if tool == "sw_interference": return "sw_interference" + ("  counting faces that touch" if a.get("touching") else "")
    if tool == "sw_export": return f"sw_export  {a.get('path')}"
    if tool == "sw_save": return f"sw_save  {a.get('path', '')}"
    if tool == "bash": return "bash  (checks the arithmetic)"
    if tool == "todo_write": return None
    return tool

def result_text(tool, raw):
    try: r = json.loads(raw)
    except Exception: return raw.split(":")[0] if raw[:3].isupper() else "ok"
    if not isinstance(r, dict): return "ok"
    if tool == "sw_build":
        m = r.get("model", {})
        size = m.get("box_mm", {}).get("size")
        bits = ["ok" if r.get("ok") else f"failed step {r.get('failed', {}).get('step')}"]
        if "volume_mm3" in m: bits.append(f"{num(m['volume_mm3'])} mm3")
        if size: bits.append(" x ".join(f"{v:g}" for v in size) + " mm")
        return ", ".join(bits)
    if tool == "sw_status": return r.get("solidworks", {}).get("release", "ok")
    if tool == "sw_new": return r.get("title", "ok")
    if tool == "sw_mass_properties": return f"{num(r.get('volume_mm3'))} mm3"
    if tool == "sw_inspect": return "box " + " x ".join(f"{v:g}" for v in r.get("box_mm", {}).get("size", [])) + " mm"
    if tool == "sw_view": return f"picture {r.get('width')}x{r.get('height')}"
    if tool == "sw_probe":
        e = (r.get("entities") or [{}])[0]
        d = f" d{e['diameter_mm']:g}" if "diameter_mm" in e else ""
        if r.get("source") == "selection":
            return f"selected: {e.get('surface', '')} {e.get('kind', '')}, {e.get('edges', '?')} edges, at y = {e.get('offset_mm', '?')}"
        return f"{len(r.get('entities', []))} found: {e.get('surface') or e.get('curve') or e.get('kind')}{d}"
    if tool == "sw_interference":
        n = r.get("interferences", 0)
        return f"{n} {'pair' if n == 1 else 'pairs'}, {num(r.get('total_volume_mm3', 0))} mm3 overlap"
    if tool == "sw_bom": return ", ".join(f"{x['file']} x{x['quantity']}" for x in r.get("items", []))
    if tool == "sw_drawing": return f"{len(r.get('views', []))} views at {r.get('sheet', {}).get('scale')}"
    if tool == "sw_export": return f"{r.get('bytes', 0):,} bytes"
    if tool == "sw_assemble": return "ok" if r.get("ok") else "failed"
    if tool == "sw_features": return f"{r.get('total')} features"
    return "ok"

log = []  # (time, line index, text) - results update their call's line
lines_at = []
calls = {}
current = []
for e in events:
    if e.get("type") == "tool_call":
        text = call_text(e["tool"], e.get("input", {}))
        if text is None: continue
        calls[e["callId"]] = len(current)
        current = current + [["dsh", text, ""]]
        lines_at.append((e["t"], [list(x) for x in current]))
    elif e.get("type") == "tool_result" and e["callId"] in calls:
        tool = next(x["tool"] for x in events if x.get("type") == "tool_call" and x.get("callId") == e["callId"])
        current[calls[e["callId"]]][2] = result_text(tool, e.get("result", ""))
        lines_at.append((e["t"], [list(x) for x in current]))
lines_at.append((t_end, [list(x) for x in current] + [["final", f, ""] for f in finals]))

def caption_at(t):
    state = []
    for when, s in lines_at:
        if when <= t: state = s
    return state

# --- drawing ------------------------------------------------------------------------------
W = 1279
TOP, BODY = (0, 27), (142, 728)
CAP_H = 150
mono = ImageFont.truetype("/usr/share/fonts/truetype/liberation/LiberationMono-Regular.ttf", 17)
bold = ImageFont.truetype("/usr/share/fonts/truetype/liberation/LiberationMono-Bold.ttf", 17)
CYAN, PINK, WHITE, DIM, BG = (34, 211, 238), (244, 114, 182), (235, 235, 235), (140, 140, 150), (13, 17, 23)
prompt_text = prompt["text"]

def window(img):
    a = img.crop((0, TOP[0], W, TOP[1]))
    b = img.crop((0, BODY[0], W, BODY[1]))
    out = Image.new("RGB", (W, (TOP[1] - TOP[0]) + (BODY[1] - BODY[0])))
    out.paste(a, (0, 0)); out.paste(b, (0, TOP[1] - TOP[0]))
    return out

def compose(img, state, intro=False):
    win = window(img)
    canvas = Image.new("RGB", (W, win.height + CAP_H), BG)
    canvas.paste(win, (0, 0))
    d = ImageDraw.Draw(canvas)
    y0 = win.height
    d.rectangle((0, y0, W, y0 + 2), fill=CYAN)
    if intro:
        # the whole prompt, wrapped, on its own card at the start
        wrapped = textwrap.wrap(prompt_text, 106)
        box_h = 28 + 22 * len(wrapped)
        d.rectangle((40, 60, W - 40, 60 + box_h), fill=BG, outline=CYAN, width=3)
        d.text((56, 70), "you", font=bold, fill=CYAN)
        for k, line in enumerate(wrapped): d.text((100, 70 + 22 * k), line, font=mono, fill=WHITE)
    y = y0 + 12
    d.text((16, y), "you", font=bold, fill=CYAN)
    d.text((64, y), (prompt_text[:110] + " ...") if len(prompt_text) > 114 else prompt_text, font=mono, fill=DIM)
    y += 26
    shown = state[-4:]
    for who, text, res in shown:
        if who == "final":
            d.text((16, y), "dsh", font=bold, fill=PINK)
            d.text((64, y), text[:118], font=bold, fill=WHITE)
        else:
            d.text((16, y), "dsh", font=bold, fill=PINK)
            d.text((64, y), text[:70], font=mono, fill=WHITE)
            if res: d.text((64 + 10 * min(len(text), 70) + 20, y), "-> " + res[:52], font=mono, fill=CYAN)
        y += 26
    return canvas

# --- time warp ----------------------------------------------------------------------------
def thumb(path):
    return np.asarray(Image.open(f"{session}/{path}").convert("L").resize((96, 60)), dtype=np.float32)

keep = []  # (image path, caption state, output seconds)
prev, prev_cap = None, None
for k, (t, path) in enumerate(frames):
    nxt = frames[k + 1][0] if k + 1 < len(frames) else t + 250
    dt = (nxt - t) / 1000
    th = thumb(path)
    cap = caption_at(t)
    changed = prev is None or float(np.abs(th - prev).mean()) > 1.2
    cap_changed = cap != prev_cap
    speed = 1.6 if changed else 10.0
    dur = dt / speed
    if cap_changed: dur = max(dur, 0.9)  # long enough to read a new line
    if keep and not changed and not cap_changed:
        keep[-1][2] += dur  # merge into the frame shown already
    else:
        keep.append([path, cap, dur])
        prev = th
    prev_cap = cap
keep[-1][2] += 4.5  # hold the ending
total = sum(x[2] for x in keep)
print(f"{len(frames)} recorded frames -> {len(keep)} clip frames, {total:.1f} s")

# --- render -------------------------------------------------------------------------------
work = f"/tmp/compose-{os.path.basename(stem)}"
os.makedirs(work, exist_ok=True)
for f in os.listdir(work): os.remove(os.path.join(work, f))
concat = []
intro = compose(Image.open(f"{session}/{keep[0][0]}"), [], intro=True)
intro.save(f"{work}/0000.png"); concat.append(("0000.png", 4.5))
for n, (path, cap, dur) in enumerate(keep, 1):
    compose(Image.open(f"{session}/{path}"), cap).save(f"{work}/{n:04d}.png")
    concat.append((f"{n:04d}.png", dur))
with open(f"{work}/list.txt", "w") as fh:
    for name, dur in concat: fh.write(f"file '{name}'\nduration {max(dur, 0.04):.3f}\n")
    fh.write(f"file '{concat[-1][0]}'\n")

run = lambda *c: subprocess.run(c, check=True, capture_output=True)
run("ffmpeg", "-y", "-f", "concat", "-safe", "0", "-i", f"{work}/list.txt", "-vf", "fps=12,format=yuv420p,scale=1280:-2:flags=lanczos",
    "-c:v", "libx264", "-crf", "24", "-preset", "slow", "-movflags", "+faststart", f"{stem}.mp4")
run("ffmpeg", "-y", "-f", "concat", "-safe", "0", "-i", f"{work}/list.txt", "-vf",
    f"fps=8,scale={width}:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=200:stats_mode=diff[p];[b][p]paletteuse=dither=sierra2_4a:diff_mode=rectangle",
    f"{work}/raw.gif")
run("gifsicle", "-O3", "--lossy=35", f"{work}/raw.gif", "-o", f"{stem}.gif")
for ext in ("gif", "mp4"): print(f"{stem}.{ext}", os.path.getsize(f"{stem}.{ext}") // 1024, "KB")
