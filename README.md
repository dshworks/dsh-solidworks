<div align="center">

```
   ___ _      __     dsh-solidworks
  / __| \    / /
  \__ \\ \/\/ /      SOLIDWORKS for DeepSeek Harness:
  |___/ \_/\_/       read, edit, rebuild, render and export the model on your desk
```

**dsh drives the SOLIDWORKS you already have open: 2016 and later, on the same PC or in a Windows VM on your Mac.**

</div>

<p align="center"><strong>
13 tools · verified on SOLIDWORKS 2016 SP1 and 2019 SP0 · one bridge exe, no runtime to install, no admin · read-only by default · MIT
</strong></p>

<p align="center">
<a href="https://github.com/dshworks/dsh-solidworks/actions/workflows/ci.yml"><img src="https://github.com/dshworks/dsh-solidworks/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
<a href="https://www.npmjs.com/package/@dshworks/dsh-solidworks"><img src="https://img.shields.io/npm/v/@dshworks/dsh-solidworks?color=CB3837&logo=npm&logoColor=white" alt="npm"></a>
<a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-3DA639" alt="MIT"></a>
<img src="https://img.shields.io/badge/SOLIDWORKS-2016%2B-DA291C" alt="SOLIDWORKS 2016 and later">
<img src="https://img.shields.io/badge/dsh-0.2.0--rc.2%20verified-1E90FF" alt="verified against dsh 0.2.0-rc.2">
</p>

<p align="center">
<a href="#a-session">A session</a> ·
<a href="#get-started">Get started</a> ·
<a href="#tools">Tools</a> ·
<a href="#what-it-will-not-do">What it will not do</a> ·
<a href="#how-it-works">How it works</a> ·
<a href="#proof">Proof</a> ·
<a href="#when-to-skip-it">When to skip it</a> ·
<a href="llms.txt">llms.txt</a> ·
<a href="README.zh.md">中文</a>
</p>

---

You ask dsh about a part, an assembly or a drawing. It opens the file in your
SOLIDWORKS, read-only, and answers from the model itself: dimensions and
equations, custom properties, mass, the feature tree, what fails to rebuild. It
can change values in memory and rebuild, look at the result as a rendered
picture, and write a STEP, PDF or native copy back into your project. Your
file on disk does not change unless you turn that on.

## A session

A real run (dsh 0.2.0-rc.2, `deepseek-flash`, SOLIDWORKS 2019 SP0 in a
Parallels VM, dsh on the Mac side). The prompt:

> Check the connection, then open C:\dshsw\samples\box.sldprt. Tell me its
> bounding box size in mm, its mass in kg and its material. Render an isometric
> view and say in one sentence what the part looks like. Export it as box.step.
> Then close the part.

dsh called `sw_status`, `sw_open`, `sw_inspect`, `sw_mass_properties`,
`sw_view` (twice, isometric and front), `sw_features`, `sw_export` and
`sw_close`, and answered:

> **Bounding box:** 100 × 100 × 100 mm. **Mass:** 0.2305 kg (volume 230,500
> mm³, density 1000 kg/m³). **Material:** none assigned. **Isometric view:** a
> hollow, open-fronted 100 mm cube with thin walls, like a box-shaped cup with
> one face removed. **Export:** `C:\dshsw\out\box.step`, 25,726 bytes.

"Open-fronted" is not in any number the tools returned; the model read it off
the rendered view. The STEP file arrived in the dsh workspace as
`solidworks/box.step`, checked against the bridge's SHA-256.

## Get started

You need dsh 0.2.0-rc.2 or later (its `dsh plugin add` uses pnpm, so pnpm on
the PATH) and a running SOLIDWORKS 2016 or later.

### dsh and SOLIDWORKS on the same Windows PC

```
dsh plugin --profile web add @dshworks/dsh-solidworks
```

Start SOLIDWORKS, start dsh, ask. The plugin runs its bundled `swbridge.exe`
on the first SOLIDWORKS question; there is nothing to pair or install.

### dsh on a Mac, SOLIDWORKS in a Windows VM

1. On the Mac: `dsh plugin --profile web add @dshworks/dsh-solidworks`, then
   start dsh once. It listens for the bridge and writes a pairing kit.
2. In the VM, signed in as the person who uses SOLIDWORKS, open
   `\\Mac\Home\.dsh\dsh-solidworks\windows\` (Parallels shares your Mac home
   folder there) and double-click `pair.cmd`.

That installs the bridge for that Windows user, connects it to dsh, and starts
it at every sign-in. No administrator, no firewall rule: the bridge dials out
to dsh, so Windows never opens a port. If you ask dsh before pairing, the
answer says where the kit is.

On VMware, another PC, or a Mac without Parallels sharing, copy the
`~/.dsh/dsh-solidworks/windows/` folder to the Windows machine and run
`pair.cmd` there. If the VM reaches your Mac on an address the kit did not
list, set it:

```yaml
# ~/.dsh/profiles/web/cordis.patch.yml
- id: dsh-solidworks
  config:
    advertise: ['10.211.55.2']   # Parallels' usual host address
```

Undo it in the VM with `%LOCALAPPDATA%\dsh-solidworks\swbridge.exe uninstall`.

### Any other MCP client, on the Windows PC

`swbridge.exe mcp` is a plain stdio MCP server with the same 13 tools:

```
claude mcp add solidworks -- C:\tools\swbridge.exe mcp
```

The exe is in this package at `bin/swbridge.exe` and on every
[GitHub release](https://github.com/dshworks/dsh-solidworks/releases) with its
SHA-256.

## Tools

Lengths are millimetres, angles degrees, mass kilograms, whatever units the
document uses; field names say so (`value_mm`, `mass_kg`). `doc` is a path,
file name or title of an open document and defaults to the active one.

| Tool | What it does |
| --- | --- |
| `sw_status` | Release (2016 SP1, 2019 SP0, ...), every SOLIDWORKS instance, the open documents, and any dialog SOLIDWORKS is waiting on. Called first. |
| `sw_open` | Open a `.sldprt`, `.sldasm` or `.slddrw`, read-only unless asked, and activate it. |
| `sw_inspect` | Type, units, configurations, material, custom properties, bounding box, body and component counts. |
| `sw_features` | The feature tree with suppression state and rebuild errors. |
| `sw_parameters` | Every dimension (`D1@Sketch1`, value, unit, driven or not) and equation, global variables marked. |
| `sw_components` | An assembly's component tree: file, configuration, state, fixed, position. |
| `sw_mass_properties` | Mass, volume, surface area, density, centre of mass. |
| `sw_edit` | In memory, then one rebuild: configuration, suppress/unsuppress, global variables, dimensions, custom properties. Each change comes back with before, after and its own outcome. |
| `sw_rebuild` | Rebuild and list every feature that fails, with the reason. |
| `sw_export` | A copy in STEP, IGES, Parasolid, ACIS, STL, PDF, DXF/DWG, eDrawings, JPG/PNG/TIF, or native. Path, size and SHA-256 back; fetched into the dsh workspace when the bridge is on another machine. |
| `sw_save` | Save over the source file. Off unless the bridge was started with `--allow-save`. |
| `sw_close` | Close a document; unsaved changes are discarded only if this bridge made them. |
| `sw_view` | A PNG of the model from a standard view, zoomed to fit, for the model to look at. The user's view is put back afterwards. |

## What it will not do

The rules are enforced on the Windows side, where the files are. dsh can ask
for anything; only the bridge decides.

- **Change your file without being told to.** Documents open read-only by
  default, `sw_edit` works in memory, and `sw_save` is refused unless the
  bridge was started with `--allow-save`. To keep an edit, export a copy.
- **Write outside its folders.** Exports go to `Documents\dsh-solidworks`, or a
  folder you allowed with `--allow-write`. An existing file is replaced only
  when asked.
- **Throw away someone's work.** It closes a document with unsaved changes only
  if this bridge made those changes and the call says `discard`.
- **Start, close or click SOLIDWORKS.** It attaches to the SOLIDWORKS in front
  of you, never starts or quits one, and never answers a dialog: it tells you
  the dialog's text instead, because answering is your decision.
- **Run code.** No macro, script or "invoke any API" tool. Every SOLIDWORKS
  call it can make is in [`bridge/compat/members.tsv`](bridge/compat/members.tsv).
- **Put the pairing token in the model's context.** The "not connected" answer
  says where the kit is, never the token, so transcripts do not carry it.

The link between dsh and a bridge on another machine is authenticated both
ways (each side proves the token with HMAC-SHA256 over fresh nonces before a
single call crosses) but not encrypted. It is meant for a host-only VM network
or a local one; across anything else, carry it through SSH or a VPN. See
[SECURITY.md](SECURITY.md).

## How it works

```
 dsh (Windows, macOS, Linux)                        Windows, the user's desktop session
 +-------------------------------+                  +----------------------------------+
 | @dshworks/dsh-solidworks      |   same PC:       | swbridge.exe  (.NET Framework 4.8)|
 |  13 sw_* tools, built with    |   stdio MCP      |  MCP server, protocol/tools.json  |
 |  dsh's own MCP tool factory   | <--------------> |  one STA thread + OLE msg filter  |
 |  spawn | listen                |   VM / other PC: |  IDispatch late binding           |
 +-------------------------------+   the bridge     |        |                          |
              ^                      dials dsh       |        v  COM, out of process     |
              |  protocol/tools.json  (HMAC pairing) |  SOLIDWORKS 2016 ... current      |
              +----- one contract, both halves ----- +----------------------------------+
```

- **One exe for every release.** The bridge references no SOLIDWORKS interop
  assembly. Every call goes through `IDispatch` by name, so the build that
  drives 2016 drives 2019 and later, and builds on Linux CI. `dynamic` and
  PowerShell's COM adapter both fail on SOLIDWORKS objects out of process
  (`TYPE_E_ELEMENTNOTFOUND`, measured); `Type.InvokeMember` does not.
- **2016 is checked, not assumed.** `bridge/compat/members.tsv` lists every
  member the bridge calls with its interface; a test fails if the bridge calls
  anything not listed, or if a listed member is missing from the SOLIDWORKS
  2016 SP1 interop. It is how `SaveAs2` (2019 SP1+), `Get6` (2018+) and
  `IEquationMgr.GlobalVariable` (2016 SP4+) stay out.
- **The instance you are looking at.** Every SOLIDWORKS registers
  `SolidWorks_PID_<pid>` in the desktop's Running Object Table. The bridge
  picks the visible one highest in the window Z-order instead of whatever
  `GetActiveObject` returns. We found machines with four instances, three of
  them invisible and stuck on a startup dialog.
- **Busy, not hung.** All calls run on one STA thread with an OLE message
  filter that retries while SOLIDWORKS is busy. A call that outlives its budget
  returns `TIMEOUT` and says whether it may still complete; later calls fail
  fast with `SW_BUSY` instead of queueing behind it, and `sw_status` still
  answers and names the dialog SOLIDWORKS is waiting on.
- **Dial out, not listen.** On another machine the bridge connects to dsh.
  Windows Firewall blocks inbound by default and opening a port takes an
  administrator; an outbound connection takes neither and works from behind
  the VM's NAT. The first bridge keeps the link; a heartbeat frees it within
  seconds when that bridge goes away.
- **One contract.** `protocol/tools.json` holds the names, schemas, budgets and
  error codes. The bridge embeds it; the plugin builds dsh tools from it with
  dsh's own `createMcpToolDefinition`, so validation, error results and image
  admission behave as for any MCP server.

## Proof

Every claim on this page was run, against real SOLIDWORKS, on 2026-10-17.

| What | Result |
| --- | --- |
| Live end-to-end, SOLIDWORKS 2016 SP1 (24.1.0) | 34 of 34 checks, through the real dial-out link, with the release exe |
| Live end-to-end, SOLIDWORKS 2019 SP0 (27.0.0) | 34 of 34 checks, same exe |
| Real dsh 0.2.0-rc.2 + `deepseek-flash`, dsh on Linux, SOLIDWORKS 2019 in a VM (listen) | 9 tool calls, 9 completed, the image reached the model, the STEP was fetched and hash-checked |
| Real dsh 0.2.0-rc.2 + `deepseek-flash`, dsh on the Windows PC itself (spawn) | the plugin started its bundled bridge through dsh's subprocess service; 5 tool calls, 5 completed; no bridge left running after dsh exited |
| The published `swbridge.exe` | rebuilt from source by CI to the same SHA-256 (`bridge/swbridge.exe.sha256`) |
| Pairing from the generated `pair.cmd`, sign-in start, `uninstall` | installed with no admin, one instance under the mutex, nothing left after uninstall |
| Unit tests (Linux, no SOLIDWORKS) | 44, including the 2016 member check and both-sides contract checks |
| `swbridge selftest` (Windows, no SOLIDWORKS) | paths, units, JSON, HMAC vector shared with the plugin |

The end-to-end checks test each answer against something the tool did not say
about itself: the stock `box.sldprt` is a 100 mm cube shelled at 5 mm with one
face open, so after `sw_edit` sets its depth to 150 mm `sw_mass_properties` must
report 100·100·150 − 90²·145 = 325,500 mm³, up from 230,500; the
PNG must decode at the requested size; the fetched STEP must hash to the
bridge's SHA-256 and start with `ISO-10303-21;`; the refusals must return
their codes (`PATH_DENIED`, `FILE_EXISTS`, `SAVE_DISABLED`, `DIRTY`,
`WRONG_DOC_TYPE`). On 2016 the run used a SOLIDWORKS that someone else had a
58-document assembly open in; a check confirms their documents, dirty flags
and active document came back unchanged.

Measured through the link, SOLIDWORKS under x64 emulation on Windows 11 ARM64:
open a part 1.1-1.5 s, read its parameters 0.2-0.5 s, render a view 0.7-1.0 s,
export STEP 0.3-0.7 s, a drawing to PDF 0.8-3.0 s.

Not verified yet: SOLIDWORKS 2020 and later, Windows 10, x64 hardware. Reports
welcome; `swbridge status` is the first thing to paste.

## When to skip it

- **You want the agent to model from scratch.** This version reads, edits
  existing values, rebuilds and exports. It does not sketch or create features.
- **Your files are STEP or IGES.** `sw_open` opens SOLIDWORKS documents; import
  them in SOLIDWORKS first.
- **You need it in the SOLIDWORKS window.** There is no task pane; the
  conversation is in dsh.
- **You want to walk 50,000 faces.** Out-of-process COM costs a round trip per
  call. The tools return summaries; geometry-heavy work belongs in an add-in or
  on an exported STEP.
- **You run several SOLIDWORKS machines into one dsh at once.** One bridge holds
  the link at a time.

## Troubleshooting

| You see | It means |
| --- | --- |
| `NOT_CONNECTED` | No bridge has paired with this dsh. Run `pair.cmd` in the VM; if it ran, check `%LOCALAPPDATA%\dsh-solidworks\bridge.log` there. |
| `SW_NOT_RUNNING` | No SOLIDWORKS in that Windows session, or it is still starting. A second SOLIDWORKS session on one machine stops at "A journal file could not be created" until someone clicks OK. |
| `SW_BUSY` / `TIMEOUT` with a dialog's text | SOLIDWORKS is waiting for a person. Answer the dialog on the Windows desktop. |
| `OPEN_FAILED: saved by a newer SOLIDWORKS` | Files open only in the release that saved them or later. |
| `another bridge holds the link` (in the bridge log) | A second machine is paired with the same dsh; the first one connected keeps it. |

## Configuration

The plugin row, `id: dsh-solidworks`:

| Key | Default | |
| --- | --- | --- |
| `link` | `auto` | `spawn` (dsh on the SOLIDWORKS PC), `listen` (bridge elsewhere); `auto` picks spawn on Windows |
| `listen.host` / `listen.port` | `0.0.0.0` / `47321` | where dsh listens for a bridge; it answers only one that proves the token |
| `advertise` | `[]` | addresses written into `pair.cmd`; empty lists this machine's, VM networks first |
| `token` | `''` | empty keeps a generated one in `$DSH_HOME/dsh-solidworks/pairing.json` |
| `bridge.out`, `bridge.allowWrite`, `bridge.allowSave`, `bridge.pid` | | spawn mode: passed to the bridge as its flags |
| `fetchDir` | `solidworks` | where exports from a remote bridge land in the workspace; empty keeps them on Windows |
| `startupWaitMs` | `15000` | how long a call right after dsh starts waits for the bridge to reconnect |

The bridge, `swbridge.exe`: `mcp`, `connect`, `status`, `uninstall`, with
`--out`, `--allow-write`, `--allow-save`, `--pid`. `swbridge help` has the rest.

## Prior art

- [wzyn20051216/solidworks-automation-skill](https://github.com/wzyn20051216/solidworks-automation-skill)
  (MIT): the broadest open SOLIDWORKS MCP server, a 61-tool Python server with a
  careful authenticated HTTP transport. Python plus pywin32 on the CAD machine,
  every tool in every turn.
- [eyfel/mcp-server-solidworks](https://github.com/eyfel/mcp-server-solidworks)
  (SolidPilot, AGPL-3.0) and
  [tian-yu200/dsh-solidworks](https://github.com/tian-yu200/dsh-solidworks)
  (AGPL-3.0): the best modelling ideas, targeting SOLIDWORKS 2026 with an
  interop built per release.
- [Slacker-LLC/solidworks-mcp](https://github.com/Slacker-LLC/solidworks-mcp)
  (Apache-2.0): clean code; uses members that 2016 lacks.

What this one does differently: a 2016 floor that a test enforces, one exe with
no runtime to install, a link that needs no open port, and dsh's own tool
plumbing instead of a parallel one. No code was copied from any of them.

## Development

```
npm install && npm test              # plugin + contract + 2016 member check, no Windows needed
npm run build:bridge                 # .NET 8 SDK; builds bin/swbridge.exe for net48
SW_LINK_TOKEN=... node tests/live/e2e.mjs   # live run against a bridge that dials this machine
```

`bridge/compat/README.md` explains how `sw2016.tsv` is regenerated from an
installed interop. MIT, see [LICENSE](LICENSE).
