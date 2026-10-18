# Security

dsh-solidworks lets an agent act on a CAD session. What it can and cannot do is
decided on the Windows side, by `swbridge.exe`, because that is where the files
are.

## What the bridge can do

It runs as the signed-in Windows user, with that user's rights, and talks to the
SOLIDWORKS running in the same desktop session. Through the 13 tools it can open
documents the user can read, change them in memory, rebuild, render views, and
write exports.

## What it refuses

- Saving over a source document, unless started with `--allow-save`. Documents
  open read-only by default.
- Writing anywhere but its output folder (`Documents\dsh-solidworks`) or a folder
  given with `--allow-write`. Paths are resolved first, so `..` cannot escape.
  An existing file is replaced only when the call asks for it.
- Discarding unsaved changes it did not make.
- Starting, closing or answering SOLIDWORKS dialogs.
- Running code: there is no macro, script or arbitrary-API tool. The complete list
  of SOLIDWORKS members it calls is `bridge/compat/members.tsv`.
- Sending any file over the link except the exports it wrote in this session,
  identified by their SHA-256 and re-hashed before sending.

## The link to another machine

When dsh runs elsewhere, the bridge connects out to dsh; Windows opens no port.
Before any call, both sides prove they hold the pairing token: HMAC-SHA256 over a
nonce from each side, so the token never crosses the wire, a replay fails, and a
listener that does not hold the token is refused by the bridge. The first bridge
to pair holds the link; others are refused.

The link is **not encrypted**. Tool calls, model data and exported files cross in
the clear. That is acceptable on a host-only VM network (Parallels, VMware) or a
trusted LAN. Across anything else, run it through SSH (`ssh -R`) or a VPN.

dsh listens on all interfaces by default so a VM can reach it. A stranger who
reaches the port gets a refusal and nothing else; set `listen.host` to your VM
network address to narrow it.

## The pairing token

dsh generates it into `$DSH_HOME/dsh-solidworks/pairing.json` (mode 0600) and
writes it into `windows/pair.cmd` next to it. On Windows it is saved in
`%LOCALAPPDATA%\dsh-solidworks\bridge.json`, readable by that user only, and kept
out of the sign-in command. It never appears in a tool result, so it is not in
transcripts. Anyone holding it can pair a bridge, or impersonate dsh to one; to
rotate it, delete `pairing.json`, restart dsh and run the new `pair.cmd`.

## Reporting

Open a private advisory at
https://github.com/dshworks/dsh-solidworks/security/advisories/new.
