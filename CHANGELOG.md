# Changelog

## 0.2.1 - 2026-10-18

Fixes found by recording the README's clips: real sessions, watched.

- Fixed: a document name with a SOLIDWORKS extension could resolve to another
  document with the same stem. With a part `flange` and its drawing open,
  `sw_export {doc: "flange.slddrw"}` exported the part, and the model spent
  twenty calls working out why the PDF was wrong. An exact path, file name or
  title now always wins, and a `.sldprt`, `.sldasm` or `.slddrw` name only
  matches that kind of document.
- `sw_build`: a cut sketched on a plane at the body's boundary points away from
  the material and removed nothing; the model needed three calls to find
  `reverse`. With no direction given, a cut that removes nothing is now made the
  other way, and the step says `reversed`.
- In documents the bridge opened or made, `sw_build` and `sw_assemble` zoom to
  fit afterwards, so someone watching SOLIDWORKS sees what was built. A user's
  own documents keep the view they set.
- Plugin: when the bridge on another machine is older than the plugin (paired
  before an update) and does not know a tool, the answer says to run `pair.cmd`
  again, instead of "unknown tool".
- `sw_build`'s description and the system-prompt guide give a concrete example
  of Y-up (a 12 mm plate on the top plane has its top face at y = 12), after a
  session put a top face at z = 12.
- README leads with four clips of real sessions; how they are made is in
  `scripts/clips/`. CONTRIBUTING.md and a bug report form.
- Live: 66 of 66 checks on SOLIDWORKS 2016 SP1 and 2019 SP0 with this exe,
  including the two fixes above.
- Reproducible bridge build: `swbridge.exe` SHA-256
  `fd6ec61c1e1200f3d45f618bdeed1a1140e78a88ecb68185bffe8a6b7fbd304b` (SDK 8.0.426).

## 0.2.0 - 2026-10-18

From reading models to making them: 8 new tools, 21 in all.

- `sw_new`: a new part or assembly from SOLIDWORKS' default template. When the
  default-template preference is empty (it was on both test installs), the
  template SOLIDWORKS would pick, then the first one in its template folders.
- `sw_build`: model a part in steps: sketches (line, centerline, polyline,
  rectangle, circle, arc) on a plane or a face, extrude and cut (blind, through
  all, both directions, mid-plane, draft), revolve, fillet, chamfer, shell,
  offset plane, delete. Every coordinate is a model-space point in mm, mapped
  into each sketch through the sketch's own transform. Faces and edges are picked
  by the closest entity to a point, never through the selection or the view. A
  failing step rolls back the call. It deletes only features it built, keeps a
  user's sketch that one of them consumed, refuses names already in the part
  before making anything, and refuses to build above the rollback bar.
- `sw_probe`: the face, edge or vertex at a point, or the user's current
  selection, with surface or curve type, area or length, normal or axis,
  diameter and owning feature; for two, the distance and angle between them.
- `sw_study`: up to 50 variants of dimensions and global variables, each from
  the original values, rebuilt and measured; every value restored and checked.
- `sw_assemble`: insert saved parts with their origin at a point, seven mate
  types between planes, faces, edges and vertices, fix and float.
- `sw_interference`: overlapping component pairs with their shared volume.
- `sw_bom`: bill of materials, flattened or top-level, with custom properties
  and an optional CSV that is fetched into the workspace like an export.
- `sw_drawing`: a drawing of a saved model, views placed in first- or
  third-angle projection at the largest standard scale that fits above the title
  block, model dimensions imported without duplicates.
- `sw_view` renders up to six views on one labelled picture, in a chosen display
  mode, and no longer crops: SaveBMP kept the window's framing and cut the sides
  off a narrower bitmap, so pictures are now taken at the window's aspect ratio
  and letterboxed.
- `sw_save` with a path saves as a new native file in an allowed folder; that is
  how new documents get a file. Saving over a source still needs `--allow-save`.
- Fixed: `sw_parameters` named a dimension after the feature it was displayed
  under. An extrude displays the offset of its sketch plane, so `D1@Above`
  appeared as a second `D1@Boss-Extrude1` and hid the real one. Names now come
  from the dimension's full name.
- `bridge/compat/members.tsv` grew from 92 to 200 members, all present in the
  SOLIDWORKS 2016 SP1 interop; a new test fails if any call names its member
  other than as a literal, which the check could not see.
- Reproducible bridge build: `swbridge.exe` SHA-256
  `18716d1b5e45ee3c37001321930952301a6f7fa4b4b7adc0f9cf63c359e5d3b5` (SDK 8.0.426).

Checked on dsh 0.2.0-rc.2, SOLIDWORKS 2016 SP1 and 2019 SP0, Windows 11 ARM64
(Parallels, x64 emulation): 64 of 64 live checks on each.

## 0.1.0 - 2026-10-17

First release.

- 13 tools: `sw_status`, `sw_open`, `sw_inspect`, `sw_features`, `sw_parameters`,
  `sw_components`, `sw_mass_properties`, `sw_edit`, `sw_rebuild`, `sw_export`,
  `sw_save`, `sw_close`, `sw_view`. Contract in `protocol/tools.json`.
- `swbridge.exe` (.NET Framework 4.8, late-bound IDispatch, one STA thread with an
  OLE message filter), MCP over stdio or over a link it dials to dsh.
- Spawn mode (dsh on the Windows PC) and listen mode (dsh elsewhere; pairing kit
  `pair.cmd`, mutual HMAC-SHA256 pairing, first bridge keeps the link, heartbeat).
- Read-only opens, confined exports, `--allow-save` for saving over sources, no
  discarding of work the bridge did not make, dialogs reported and never answered.
- SOLIDWORKS 2016 floor enforced by a test against the 2016 SP1 interop
  (`bridge/compat/`).
- Reproducible bridge build: `swbridge.exe` SHA-256
  `e21b7b647ea0a04cb192e5b3c3533d7bcf8bee68bc71ca5e0568c4131bc2ec24` (SDK 8.0.426).

Checked on dsh 0.2.0-rc.2, SOLIDWORKS 2016 SP1 and 2019 SP0, Windows 11 ARM64
(Parallels, x64 emulation).
