# 10_FIX50_FP_QF.xml — EP302 Rebuild Verification Report

**Date:** 2026-04-04
**File:** `spec/FixPortal/10_FIX50_FP_QF.xml` (53,219 lines; not present in this branch, see the note in the merge report)
**Sources:** `FIX_Latest_EP302.xml` (Orchestra EP302, 2026-03-20) + `9_FIX44_FP_QF.xml` (FixPortal additions)

---

## 1. Summary

| Phase | Result |
|---|---|
| Fields (6,604) | **PASS** |
| Components (256) | **PASS** |
| Messages (173) | **PASS** |
| Structural Integrity | **PASS** |

---

## 2. Fields Verification

### Counts

| Source | Fields |
|---|---|
| EP302 | 6,189 |
| FIX44 FixPortal | 1,628 |
| Overlapping | 1,200 |
| EP302-only | 4,989 |
| FIX44-only | 428 |
| Intentionally removed (duplicates) | 13 |
| **Output** | **6,604** (= 6,617 - 13 removed) |

### Removed Duplicates (13 FIX44 custom tags superseded by EP302 standard tags)

| Removed Tags | Reason |
|---|---|
| 22086–22091 | PartyAltID family — retired in favour of EP302 `PartyDetailAlt*` (tags 1516–1521) |
| 22250–22251 | NoPriceQualifiers / PriceQualifier — EP302 has standard tags 2709–2710 |
| 22327–22329 | FloatingRateIndexCurve* — EP302 has standard tags 2728–2730 |
| 22365–22366 | UnderlyingIndexCurve* — EP302 has standard tags 2723–2724 |

### Overlap Checks

| Check | Result |
|---|---|
| Name mismatches (output vs EP302) | **0** — PASS |
| Type mismatches (output vs EP302) | **0** — PASS |
| Duplicate tag numbers | **0** — PASS |
| Duplicate field names | **0** — PASS |

### Specific Tag Overrides

| Tag | Name | Type | Enums | Status |
|---|---|---|---|---|
| 47 | Rule80A | STRING | 30 (incl. multi-char NC, CL, etc.) | **PASS** |
| 327 | HaltReason | INT | 0–5 only (no FIX44 CHAR enums) | **PASS** |
| 800 | OrderBookingQty | QTY | — | **PASS** |

### Enum Merge Samples (10 of 83 checked)

| Tag | Name | FIX44 Extras | Status |
|---|---|---|---|
| 22 | SecurityIDSource | 2 | **PASS** |
| 65 | SymbolSfx | 4 | **PASS** |
| 139 | MiscFeeType | 4 | **PASS** |
| 150 | ExecType | 3 | **PASS** |
| 167 | SecurityType | 5 | **PASS** |
| 201 | PutOrCall | 1 | **PASS** |
| 221 | BenchmarkCurveName | 5 | **PASS** |
| 234 | StipulationValue | 14 | **PASS** |
| 235 | YieldType | 2 | **PASS** |
| 305 | UnderlyingSecurityIDSource | 1 | **PASS** |

---

## 3. Components Verification

### Counts

| Source | Components |
|---|---|
| EP302 | 167 |
| FIX44 FixPortal | 128 |
| Shared | 37 |
| EP302-only | 130 |
| FIX44-only (carried) | 89 (91 minus 2 retired) |
| **Output** | **256** |

### Retired Components

| Component | Status |
|---|---|
| `PartyAltIDGrp` | Absent — **PASS** |
| `PartyAltSubGrp` | Absent — **PASS** |

### FIX44-Only Custom Components (all present)

`Algos_BMOA`, `Algos_Berenberg`, `HedgeData`, `Interlisted_Settlement`, `ExecRuleNameGrp`, `Notes`, `ClientCustomDataGrp`, `CompDealerQuoteGrp`, `RefPriceGrp`, `ReferenceIDGrp` — **PASS**

### Shared Component Spot-Checks (10 of 37)

| Component | EP302 Fields | FIX44 Additions | Total | Status |
|---|---|---|---|---|
| Instrument | 174 | +16 (required="N") | 190 | **PASS** |
| UnderlyingInstrument | 210 | +11 | 221 | **PASS** |
| InstrumentLeg | 172 | +8 | 180 | **PASS** |
| OrderQtyData | 5 | +2 | 7 | **PASS** |
| CommissionData | 7 | +3 | 10 | **PASS** |
| SpreadOrBenchmarkCurveData | + | + | + | **PASS** |
| YieldData | + | + | + | **PASS** |
| FinancingDetails | + | + | + | **PASS** |
| SettlInstructionsData | + | + | + | **PASS** |
| FloatingRateIndex | + | + | + | **PASS** |

### Required Attribute Overrides

| Component | Field | Value | Status |
|---|---|---|---|
| Instrument | Symbol | required="N" | **PASS** |
| UnderlyingInstrument | UnderlyingSymbol | required="N" | **PASS** |

### Component Reference Integrity

| Check | Result |
|---|---|
| Referenced but undefined | **0** — PASS |
| Defined but unreferenced | 6 (informational) |

Unreferenced: `StandardHeader`, `StandardTrailer` (implicit in QuickFIX), `LegBenchmarkCurveData`, `ClearingInstructionsGrp`, `DealerGrp`, `RegulatorTradeIDGrp`

---

## 4. Messages Verification

### Counts

| Source | Total | Admin | Non-Admin |
|---|---|---|---|
| EP302 | 173 | 0 | 173 |
| FIX44 FixPortal | ~95 | 8 | ~87 |
| **Output** | **173** | **0** | **173** |

Admin messages (0, 1, 2, 3, 4, 5, A, n) correctly excluded — **PASS**

### OrigClOrdID `required=` Verification

| Message | Output | Status |
|---|---|---|
| OrderCancelRequest (F) | **required="Y"** | **PASS** (override applied) |
| OrderCancelReject (9) | **required="N"** | **PASS** (EP302 wins) |

### EP302 Message Names

| MsgType | Output Name | Status |
|---|---|---|
| 6 | IOI (not IndicationOfInterest) | **PASS** |
| b | MassQuoteAck (not MassQuoteAcknowledgement) | **PASS** |
| BC | NetworkCounterpartySystemStatusRequest | **PASS** |
| BD | NetworkCounterpartySystemStatusResponse | **PASS** |

### Critical Message Deep Verification

| Message | EP302 Base | FixPortal additions | Inline→Component | Status |
|---|---|---|---|---|
| NewOrderSingle (D) | All present | ~28 appended | Parties, UndInstrmtGrp, PreAllocGrp | **PASS** |
| ExecutionReport (8) | All present | ~134 appended | Parties, UndInstrmtGrp | **PASS** |
| OrderCancelRequest (F) | All present | 5 appended | Parties, UndInstrmtGrp | **PASS** |
| OrderCancelReplaceRequest (G) | All present | ~23 appended | Parties, UndInstrmtGrp, PreAllocGrp | **PASS** |
| OrderCancelReject (9) | All present | 7 appended | Parties | **PASS** |

### FIX44-Only Non-Admin Messages

**0** — all FIX44 non-admin msgtypes also exist in EP302. Nothing lost — **PASS**

---

## 5. Structural Validation

| Check | Result |
|---|---|
| XML well-formedness | **PASS** |
| Root attributes (`major="5"`, `minor="0"`, `type="FIX"`, `servicepack="2"`, `name="FIX50_FP_QF"`) | **PASS** |
| Header present and empty | **PASS** |
| Trailer present and empty | **PASS** |
| Section ordering (header, trailer, messages, components, fields) | **PASS** |
| Field refs in messages → field definitions (6,027 refs) | **PASS** |
| Component refs in messages → component definitions (250 refs) | **PASS** |
| Group names → field definitions (531 groups) | **PASS** |
| No duplicate tag numbers (6,604 unique) | **PASS** |
| No duplicate field names (6,604 unique) | **PASS** |
| No duplicate component names (256 unique) | **PASS** |
| No duplicate message types (173 unique) | **PASS** |
| Orphan fields | 47 (informational — session fields, custom FixPortal fields) |
| Orphan components | 6 (informational — StandardHeader/Trailer + 4 unused) |

---

## 6. Verdict

**ALL CHECKS PASS.** The rebuilt `10_FIX50_FP_QF.xml` is structurally sound, referentially complete, and correctly implements all merge rules from the EP302 base with FIX44 FixPortal additions.

### Key Improvements Over Previous Version

- Based on authoritative **EP302** source (not outdated base FIX 5.0 SP2)
- **6,604 fields** (up from ~2,228) — full EP coverage
- **173 messages** (up from 110) — all EPs included
- No tag collisions at 1506, 1586–1605 (EP302 has correct definitions)
- No RelationshipRisk orphans
- No duplicate field names
- All inline groups properly converted to component references
