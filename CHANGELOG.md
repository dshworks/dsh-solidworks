# Changelog

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
