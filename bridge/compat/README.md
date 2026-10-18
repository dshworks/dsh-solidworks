# SOLIDWORKS 2016 compatibility check

`members.tsv` is every SOLIDWORKS API member `swbridge.exe` calls, with the
interface it is called on. `sw2016.tsv` is every member of those interfaces in
the SOLIDWORKS 2016 SP1 interop (`SolidWorks.Interop.sldworks.dll` 24.1.0.45),
names only. `tests/contract.spec.mjs` fails when:

- the bridge calls a member that is not in `members.tsv`, or
- a row in `members.tsv` is missing from `sw2016.tsv`.

The bridge is late-bound, so a member SOLIDWORKS 2016 lacks would fail at run
time on a customer's machine, not at build time. This check moves that failure
to CI.

## Adding a call

Add the member and its interface to `members.tsv`. If the interface is new,
regenerate `sw2016.tsv` from a 2016 interop (from any SOLIDWORKS 2016 install,
`<install>\api\redist\SolidWorks.Interop.sldworks.dll`; it is not in this repo
and must not be):

```
dotnet run --project bridge/compat/reflect -- <path-to-2016-dll> \
  $(grep -v '^#' bridge/compat/members.tsv | cut -f1 | sort -u) > rows.tsv
```

then put the two header comment lines back on top of `rows.tsv` as `sw2016.tsv`.
`reflect` reads metadata only; it runs on any OS without SOLIDWORKS.
