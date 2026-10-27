# DDTool
DataDictionary analyzer/codegen for QuickFIX/n

It intentionally has minimal dependencies, and the UTs use
Microsoft's default TestFramework.

## To run

To parse DDs, but only analyze (not generate):  
`> dotnet run --project DDTool <ddFile> <ddFile>...`

To parse DDs and generate:  
`> dotnet run --project DDTool --reporoot <qfRepoDir> --outputdir <destDir> <ddFile> <ddFile>...`

Typically invoked via the wrapper script:  
`> pwsh scripts/Generate-Message-Sources.ps1`

## FixPortal Changes

### DDTool modifications

The only DDTool modification is in `DDTool/Structures/DataDictionary.cs`, which adds an
ordering enhancement to ensure specs are processed in the correct order during field
aggregation: `Identifier` prepends a sort key from a hard-coded table (`_fixPortalDesiredOrder`:
FIX 4.0=1, 4.1=2, 4.3=3, 5.0=4, 5.0 SP1=5, 5.0 SP2=6, FIXT 1.1=7, 4.2 and 4.4=8) and
`IdentifierNoDots` strips it again so generated names stay `FIX44` etc. DDTool itself is otherwise
unmodified from upstream (diff against tag `v1.14.1`) — the FixPortal specs
use only standard FIX field types that upstream DDTool already supports.

### Spec file organisation

FixPortal data dictionaries live in `spec/FixPortal/` (a legacy directory name kept
for path stability) rather than `spec/fix/`. Files are numbered with a prefix that
`scripts/Generate-Message-Sources.ps1` sorts on before passing them to DDTool. DDTool then
orders the dictionaries itself by `Identifier` (table above), not by file name, which matters because
its field aggregation uses "latest version wins" semantics. Note the file prefixes and the
internal sort keys differ (the SP2 file is `10_` but sorts as 6):

```
1_FIX40.xml          Standard FIX 4.0
2_FIX41.xml          Standard FIX 4.1
3_FIX43.xml          Standard FIX 4.3
4_FIX50.xml          Standard FIX 5.0
5_FIX50SP1.xml       Standard FIX 5.0 SP1
7_FIXT11.xml         Standard FIXT 1.1
8_FIX42_FP_QF.xml    Customised FIX 4.2 (replaces standard FIX 4.2)
9_FIX44_FP_QF.xml    Customised FIX 4.4 (replaces standard FIX 4.4)
10_FIX50SP2_FP_QF.xml   Customised FIX 5.0 SP2 (replaces standard FIX 5.0 SP2)
```

Customised specs carry a `name` attribute on the root `<fix>` element (e.g.
`name="FIX42_FP_QF"`), but DDTool ignores it: the duplicate-name check uses the
`customname` attribute (`Parsers/VersionParser.cs:33`), which no spec here sets.
The customised and standard dictionaries do not clash because only `spec/FixPortal/`
is passed to DDTool, so each FIX version appears once.

### Generation script

`scripts/Generate-Message-Sources.ps1` is modified from upstream to:

- Read specs from `spec/FixPortal/` instead of `spec/fix/`
- Sort input files by their numeric prefix to ensure deterministic processing order
