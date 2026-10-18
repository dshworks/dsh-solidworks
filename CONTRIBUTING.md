# Contributing

Bug reports with `swbridge status` pasted in are the most useful thing right
now, especially from SOLIDWORKS releases and Windows versions this has not run
on yet (2020 and later, Windows 10, x64 hardware). Use the bug report form.

## The rules the code keeps

- **The Windows side decides what may be written.** A change that lets dsh save
  over a source file, write outside the allowed folders, delete a feature the
  bridge did not build, or discard someone's unsaved work will not be merged.
- **2016 is the floor.** Every SOLIDWORKS member the bridge calls is a string
  literal listed in `bridge/compat/members.tsv`, and must exist in the 2016 SP1
  interop (`bridge/compat/sw2016.tsv`). `npm test` checks both.
- **One contract.** Tools are defined once, in `protocol/tools.json`; the bridge
  embeds it and the plugin builds dsh tools from it. Keep it in its layout with
  `node scripts/format-contract.mjs`.
- **Pages may not say what the code does not do.** `tests/claims.spec.mjs`
  checks the README's numbers, tables and links; change both together.

## Running it

```
npm install && npm test        # Linux or macOS is enough: plugin, contract, 2016 members, claims
npm run build:bridge           # .NET 8 SDK (version pinned in global.json); writes bin/swbridge.exe
```

`swbridge selftest` runs the bridge's own checks on Windows without SOLIDWORKS.

The live checks need a Windows machine with SOLIDWORKS 2016 or later, copies of
the stock samples (`box.sldprt`, `box.slddrw`, `bolt.sldprt`,
`bolt-assembly.sldasm`) in one folder, and a bridge that can dial this machine:

```
SW_LINK_TOKEN=<any token> SW_SAMPLES='C:\samples' node tests/live/e2e.mjs
swbridge connect <this machine>:47321 <the same token> --out C:\out     # on Windows
```

They open only those copies, make their own documents, and close everything
they opened or made; a final check confirms the documents that were open before
are as they were. If the build changes `bin/swbridge.exe`, update
`bridge/swbridge.exe.sha256`; CI rebuilds the exe and compares.
