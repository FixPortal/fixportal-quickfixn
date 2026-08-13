# AGENTS.md

Repo-specific conventions for coding agents working in this repository. Read
`FIXPORTAL_README.md` first — it covers the fork's structure, the FixPortal
enhancement markers, and the upstream upgrade workflow. This file only adds
what an agent gets wrong without being told.

## The mainline is `fpsim`, not `master`

`master` mirrors upstream `connamara/quickfixn` and carries no FixPortal
changes. All work branches from and targets **`fpsim`**. A PR opened against
`master` is almost always a mistake.

This bites tooling as well as people: anything that defaults to "the default
branch" points at `master` and therefore at a branch nobody merges into. When
wiring a review bot, a workflow trigger, or a branch rule, name `fpsim`
explicitly.

## Engine changes are gated

Prefer flagging a risk over changing engine behaviour. Do not propose
re-aligning a file with upstream as a drive-by — divergence from
`connamara/quickfixn` in `QuickFIXn/` is usually deliberate and marked with a
`// FP Enhancement: YYYY-MM-DD — <rationale>.` banner. Removing one silently
reverts a decision.

## Data dictionaries are wire-affecting, including for versions they do not name

`spec/**` and `DataDictionaries/**` drive DDTool code generation into a
**shared, name-keyed, processing-order-dependent** namespace. The generated
field set is additive across every FIX version, so an edit scoped to one
version's dictionary can change field availability or naming for another.

The practical consequence: a tag's availability and its exact property name
must be read off the **generated message class for that version**, never off
the canonical FIX specification. Two field classes differing only in casing can
co-exist for the same tag (`IOIid` / `IOIID`, both tag 23). Treat any dictionary
edit as wire-affecting and say so explicitly in the PR.

See `~/.agents/notes/quickfixn-traps.md` for the accumulated cases.

## Generated code is not hand-edited

`Messages/**`, `QuickFIXn/Fields/Fields.cs` and `QuickFIXn/Fields/FieldTags.cs`
are DDTool output. Change the dictionary and regenerate with
`scripts/Generate-Message-Sources.ps1`; a hand edit is silently reverted by the
next generation run. Flag direct edits there rather than reviewing them as
ordinary source.

## Scaffold exceptions

This fork deliberately preserves upstream's .NET project layout to keep future
`connamara/quickfixn` merges reviewable. It therefore retains `QuickFIXn.sln`
instead of adding `.slnx`, does not add `Directory.Build.props`, a repository
`.editorconfig`, CSharpier, or `FixPortal.CodeStyle`, keeps upstream's inline
package versions instead of enabling central package management, does not add a
`.csharpierignore`, and retains upstream's `*.cs text` rule instead of forcing
`*.cs text eol=crlf`.

Each exception avoids repo-wide churn or persistent conflicts in upstream-owned
files. FixPortal-owned automation and dependencies may still be added locally
when they do not rewrite the upstream tree.

## Contributing

`CONTRIBUTING.md` is upstream's and describes contributing to
`connamara/quickfixn` — its CLA and issue links do not apply to this fork.
