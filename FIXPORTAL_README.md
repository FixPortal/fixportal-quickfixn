# FixPortal QuickFIX/n Fork

## Overview

This repository is a [FixPortal](https://github.com/FixPortal) fork of
[QuickFIX/n](https://github.com/connamara/quickfixn), the .NET implementation
of the FIX protocol engine. The fork adds custom data dictionaries, additional
FIX 5.0 SP2 support, message-rejection notification hooks, and other engine
enhancements on top of upstream releases.

For general QuickFIX/n documentation, refer to the upstream `README.md`.

## Repository structure

| Remote     | URL                                  | Purpose                       |
|------------|--------------------------------------|-------------------------------|
| `origin`   | `github.com/FixPortal/fixportal-quickfixn`     | FixPortal fork (this repo)    |
| `upstream` | `github.com/connamara/quickfixn`     | Official QuickFIX/n (GitHub)  |

### Key branches

| Branch       | Purpose                                                                      |
|--------------|------------------------------------------------------------------------------|
| `master`     | Tracks upstream `master`                                                     |
| `fpsim`      | FixPortal deployment branch — upstream `v1.14.0` + FixPortal enhancements    |

### FixPortal-specific content

- `spec/FixPortal/` — Custom data-dictionary XML files (legacy directory name)
- `scripts/Generate-Message-Sources.ps1` — Uses the custom specs
- `DDTool/Structures/DataDictionary.cs` — Ordering enhancements for spec processing
- FixPortal enhancements throughout `QuickFIXn/`, marked with a single-line banner of the form
  `// FP Enhancement: YYYY-MM-DD — <one-line rationale>.` (matching the convention used in the
  FixPortal FixAtdl repo). Older `// FixPortal Enhancement` markers and `#region FixPortal Enhancement`
  blocks may still appear in unreviewed corners; convert on touch.

## Upgrade workflow

When a new upstream release is available (e.g. `v1.15.0`), follow these steps to
incorporate it into `fpsim`.

### 1. Sync `master` with upstream

```bash
git checkout master
git fetch upstream --tags
git merge upstream/master
git push origin master
```

### 2. Merge the release into `fpsim`

```bash
git checkout fpsim
git merge v1.15.0
```

Merge conflicts are expected in core files (`Session.cs`, `Message.cs`,
`DataDictionary.cs`, `ThreadedSocketAcceptor.cs`, logger and transport).
FixPortal enhancements are marked with `// FP Enhancement: YYYY-MM-DD — <rationale>.`
banners where practical (older `// FixPortal Enhancement` markers may also appear) —
when in doubt, take the upstream change and re-apply the FixPortal modification on
top, then convert the marker to the banner format if it isn't already.

The `DDTool/Structures/DataDictionary.cs` model class (~86 lines) is distinct
from the runtime `QuickFIXn/DataDictionary/DataDictionary.cs` (700+ lines);
do not confuse them during merges.

### 3. Regenerate message sources

```powershell
pwsh scripts/Generate-Message-Sources.ps1
```

This runs DDTool against `spec/FixPortal/*.xml` and regenerates fields and
message classes. DDTool does not delete stale outputs — clean the relevant
`Messages/*/` directory before regenerating if definitions have been removed.

### 4. Build and test

```bash
dotnet build
dotnet test
```

### 5. Pack and publish

The packable projects — `QuickFIXn/QuickFix.csproj`, `Messages/*/QuickFix.*.csproj`, and
`DataDictionaries/FixPortal.QuickFIXn.DataDictionaries.csproj` (11 in total) — each carry a
`<Version>` and `<PackageId>FixPortal.QuickFIXn.*</PackageId>`.
Bump `<Version>` on all 11 (e.g. `1.14.0-fixportal.1` → `1.14.0-fixportal.2`,
or move to the new upstream base such as `1.15.0-fixportal.1` after an
upstream merge).

```bash
dotnet pack QuickFIXn.sln -c Release -o _pkgout
```

Debug symbols are embedded (`<DebugType>embedded</DebugType>`), so no separate
symbol packages are produced.

## Spec maintenance

The custom data dictionaries in `spec/FixPortal/` replace the standard
QuickFIX/n specs in `spec/fix/`. The approach:

- **Standard versions** (FIX 4.0, 4.1, 4.3, 5.0, 5.0 SP1, FIXT 1.1) are copied
  from upstream with a numeric prefix for ordering
- **Customised versions** (FIX 4.2, 4.4, 5.0 SP2) extend the standard versions
  with FixPortal-specific fields, components, and message additions
- The customised FIX 5.0 SP2 spec (`10_FIX50SP2_FP_QF.xml`) is built from the
  upstream FIX50SP2.xml with FixPortal additions merged in. `scripts/build_fix50_fp.py`
  can regenerate it from the upstream base and the FIX44 source
- Custom specs use a `name` attribute on the root `<fix>` element (e.g.
  `name="FIX50SP2_FP_QF"`) to avoid DDTool's duplicate-name check

### Known tag collisions (custom FIX44 vs FIX50SP2)

Several custom tags in the FIX44 spec collide with standard FIX50SP2 tags. When
building the custom FIX50SP2 spec, FIX50SP2 definitions take precedence:

- **Tags 1586-1605**: Custom `LegPosAmt` fields vs FIX50SP2 `RelationshipRisk` fields
- **Tag 800**: Custom `NOE` (CHAR) vs FIX50SP2 `OrderBookingQty` (QTY)
- **Tag 327**: Custom `HaltReason` (CHAR) vs FIX50SP2 `HaltReasonInt` (INT)
- **Tags 22086-22091**: Custom `PartyAltID` fields — superseded by FIX50SP2 tags 1516-1521

Custom message references to colliding fields are removed from the FIX50SP2 spec.

## Version history

The patch suffix (`-fixportal.N`) tracks the merged PR count from `fpsim`. One bump per merged PR keeps the package version a direct cross-reference to repo history. Skipped suffix numbers are PRs that didn't change shipped code (e.g. CI workflow PRs).

| Version            | Date       | Notes                                                                          |
|--------------------|------------|--------------------------------------------------------------------------------|
| 1.14.0-fixportal.1 | 2026-05    | Initial FixPortal release — fork of upstream `v1.14.0` baseline                |
| 1.14.0-fixportal.7 | 2026-05-24 | Post-audit cleanup batch: DB DD path removed, three upstream backports (`MessageFactoryNotFound`, `RedactFieldsInLogs`, `IMessageStore.SetAndIncrNextSenderMsgSeqNum`), `Enhancements.Utility.ParsePath` `{DATE:...}` regex fixed, `FileLog` day-rotation actually works now (clock injectable via `TimeProvider`), 93 files converted to file-scoped namespaces, dead public extensions deleted, all `// FixPortal Enhancement` markers re-annotated as dated banners |
| 1.14.1-fixportal.6 | 2026-08-16 | `Session.Disconnect(reason)` now stamps `SessionState.LastDisconnectReason` before invoking `Application.OnLogout`, and `Session` exposes `LastDisconnectReason` and `LogoutReason` as read-only properties, so an application can distinguish an intentional logout from an unexpected drop. Rides along without its own suffix bump: `543818f8` (`fix: serialize resend rejection history`), merged 2026-08-10 on top of `.5`, wraps the existing bounded resent-outbound-history operations (`RecordResentOutbound`/`WasResentOutbound`/`ClearResentOutboundHistory` — the 1,024-capacity FIFO with eviction was already shipped in `.5` via `1e8014c6`) in the session's own `_sync` lock alongside their existing narrower `_resentOutboundHistorySync`. This closes a race where a lifecycle-boundary clear (disconnect, `ResetOnLogon`, or a peer sequence reset) could interleave with an in-flight resend record, letting a stale entry from before the clear survive into the freshly-cleared history — observable as a spurious `referencedMessageWasResent = true` for a message on a recycled sequence number that was never actually resent in the new epoch. It does not change the 1,024-entry retention window itself. |
| 1.14.1-fixportal.4 | 2026-08-06 | Dynamically added initiator sessions now receive an immediate targeted connection attempt without waiting for the shared reconnect interval or advancing unrelated sessions' retry cadence. |
| 1.14.1-fixportal.3 | 2026-06-14 | S2 slot-spill: replaces the A-F1 re-tap approach with `IFixWireTap.OnInboundQueued` + `OnInboundReplayPrepare` + `OnInboundQueueCleared` — the engine adapter can now preserve the original `CaptureId` across a gap-fill queue drain without minting a second capture row for the same FIX frame (§12.6). `OnOutbound` gains a `bool transmitted` parameter (M1): the tap fires after `_responder.Send` in both the null-responder and live-send branches so the disposition is definitive at call time. `OnInboundReplay` removed (dead with the new mechanism). Fork unit test suite 401 pass. |
| 1.14.1-fixportal.2 | 2026-06-11 | Adversarial-audit batch 1 + batch 2 remediation: `IFixWireTap.OnInboundReplay` seam + A-F1 re-tap; A-F5 tap-before-null-responder; initiator restart-race fix; existing FP review hardening. |
| 1.14.1-fixportal.1 | 2026-06-08 | Merged upstream release `v1.14.1` — new upstream base. Adds CME Enhanced Resend (chunked resend / gap-fill handling in `Session`/`ResendRange`/`SessionState`), .NET 10 target alongside net8, and removes expired deprecations. All FixPortal enhancements re-applied on top of the upstream changes; message sources regenerated from `spec/FixPortal/*.xml`. Suffix resets to `.1` on the new upstream base. A cross-vendor adversarial review found the merge faithful (zero merge-defects). Two flagged upstream CME-resend items were re-verified as **not** defects and left matching upstream: the `Verify` `GapFillFlag` reads (unreachable with the field absent, since `NextSequenceReset` calls `Verify(sr, isGapFill, …)` — regression test added; harmless defensive `IsSetField` guards kept), and the chunked-resend `Max+1` (intent-uncertain per upstream's own `// TODO we can +1 this`). Shipped FP-side polish only: `LogExtended` atomic lookup + resend-path audit coverage, reject value-carrier wiring, and a `ulong` `RefSeqNum` read. |
