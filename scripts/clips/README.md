# How the clips are made

The clips in the README are real sessions, recorded, then captioned from the
session's own events. Nothing in them is typed in by hand.

1. **Record the SOLIDWORKS window.** `record-window.ps1` runs on the Windows PC,
   in the desktop session that shows SOLIDWORKS, and saves a JPEG of the window
   four times a second, named by its UTC time in milliseconds. It uses
   `PrintWindow`, so the window is captured even when something covers it, and
   it marks itself DPI-aware: at a high display scale a DPI-unaware recorder
   gets only the top-left part of SOLIDWORKS' window.
2. **Record the session.** dsh runs the prompt headless and `stamp.mjs` adds the
   arrival time to every event:
   `dsh headless --json "<prompt>" | node stamp.mjs >> events.jsonl`, after a
   first line `{"t": <ms>, "type": "prompt", "text": "<prompt>"}`.
3. **Line the clocks up.** `clock.json` holds `{"vm_minus_local": <ms>}`, the
   Windows clock minus this machine's, read over one round trip.
4. **Compose.** `compose.py <session dir> <out stem> --final "<words>" ...`
   crops the window to its title strip and work area and writes the caption bar
   from the events: the prompt as typed, each tool call with a summary of its
   real arguments and result, and closing lines that must appear in the
   model's final answer (it refuses otherwise, markdown aside). Stretches where
   nothing changes on screen are fast-forwarded, stretches where the window
   changes play near real time. Nothing is reordered or cut.

Needs ffmpeg, gifsicle, Python with Pillow and numpy, and Liberation Mono.

The models are drawn in the sessions from the prompts shown in the clips; no
customer or third-party model appears in them.
