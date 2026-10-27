# 10_FIX50_FP_QF.xml — EP302 Merge Report

> Historical record. `10_FIX50_FP_QF.xml` is not present in `spec/FixPortal/` (nor in this branch's history);
> the generated FIX 5.0 SP2 dictionary is `10_FIX50SP2_FP_QF.xml`, built from `spec/fix/FIX50SP2.xml` by
> `scripts/build_fix50_fp.py`. The tooling in `orchestra/` still targets the old output name.

**Date:** 2026-04-04
**Sources:** `FIX_Latest_EP302.xml` (base, Orchestra EP302) + `9_FIX44_FP_QF.xml` (FixPortal additions)
**Output:** `10_FIX50_FP_QF.xml` — FIX 5.0 application dictionary (empty header/trailer)

---

## 1. Overview

| Section | EP302 | FIX44 FixPortal | Overlap | Merged (expected) |
|---|---|---|---|---|
| Fields | 6,189 | 1,628 | 1,200 | ~6,617 |
| Components | 167 | 128 | 37 | ~258 |
| Messages | 173 (app) | 87 (80 app + 7 admin) | 80 | 173 |

---

## 2. Fields

### 2.1 Counts

| Category | Count |
|---|---|
| EP302-only | 4,989 |
| FIX44-only | 428 |
| In both | 1,200 |
| **Expected merged total** | **~6,617** |

### 2.2 Name Differences (EP302 wins)

| Tag | FIX44 Name | EP302 Name | Notes |
|---|---|---|---|
| 23 | IOIid | IOIID | Capitalisation only |
| 33 | LinesOfText | NoLinesOfText | Naming convention |
| 800 | NOE | OrderBookingQty | Known decision — use EP302 |
| 1233 | CommissionRate | CommRate | EP302 renamed |

> [!note] Action
> References to old FIX44 names in messages/components must use the EP302 name.

### 2.3 Type Differences (44 total, EP302 wins unless noted)

**MULTIPLEVALUESTRING renames (9 fields — transparent):**
Tags 18, 276, 277, 286, 291, 292, 529, 546 — EP302 type wins.

**Genuine type changes requiring review:**

| Tag | Name | FIX44 Type | EP302 Type | Notes |
|---|---|---|---|---|
| 63 | SettlType | CHAR | STRING | Safe widening |
| 82 | NoRpts | NUMINGROUP | INT | EP302 wins |
| 139 | MiscFeeType | INT | STRING | EP302 widened to string enums |
| 213 | XmlData | DATA | XMLDATA | EP302 semantic refinement |
| 239 | RepoCollateralSecurityType | INT | STRING | EP302 widened |
| 243 | UnderlyingRepoCollateralSecurityType | INT | STRING | Same as 239 |
| 327 | HaltReason | CHAR | INT | Known decision — INT, no FIX44 CHAR enums |
| 534 | NoAffectedOrders | INT | NUMINGROUP | EP302 correction |
| 576 | NoClearingInstructions | INT | NUMINGROUP | EP302 correction |
| 580 | NoDates | INT | NUMINGROUP | EP302 correction |
| 674 | LegAllocAcctIDSource | STRING | INT | EP302 wins |
| 851 | LastLiquidityInd | CHAR | INT | EP302 wins |
| 877 | UnderlyingCPProgram | STRING | INT | EP302 wins |
| 1072 | SideGrossTradeAmt | FLOAT | AMT | Semantic narrowing |
| 1075 | LegGrossTradeAmt | FLOAT | AMT | Semantic narrowing |
| 1132 | TZTransactTime | UTCTIMESTAMP | TZTIMESTAMP | Timezone semantics change |
| 1233 | CommRate | PERCENTAGE | FLOAT | EP302 wins |
| 1847 | TradeAllocCurrency | STRING | CURRENCY | EP302 wins |
| 2174 | LegIndexAnnexDate | PERCENTAGE | LOCALMKTDATE | FIX44 looks buggy — EP302 wins |
| 2192 | LegSettlMethod | CHAR | STRING | Safe widening |
| 2251 | LegComplexEventStartDate | LOCALMKTDATE | UTCDATEONLY | EP302 wins |
| 2252 | LegComplexEventEndDate | LOCALMKTDATE | UTCDATEONLY | EP302 wins |
| 2349 | PricePrecision | FLOAT | INT | EP302 wins |
| 40216 | PaymentCurrency | STRING | CURRENCY | EP302 wins |

Plus ~10 FLOAT→AMT changes and 2 LOCALMKTDATE→LOCALMKTTIME changes — all EP302 wins.

### 2.4 FIX44 Extra Enums to Append (83 fields)

Key fields with significant FixPortal enum additions:

| Tag | Name | Notable FixPortal Enums |
|---|---|---|
| 22 | SecurityIDSource | +P (Markit RED), +S (FIGI), +406 (BBG XML), +410 (BBG derivative) |
| 65 | SymbolSfx | +ILB, ILBP, PI, W |
| 139 | MiscFeeType | +100 (TAX_ON_PRINCIPAL), +101 (TAX_ON_ACCRUED), +1026, +1028 |
| 150 | ExecType | +1 (PARTIAL_FILL), +2 (FILLED), +z (PENDING_TRADE) — deprecated FIX42 values for backward compat |
| 167 | SecurityType | +35 types (ARS, BSKT, CDI, DERIV, etc.) |
| 452 | PartyRole | +1000 (SALESPERSON), +1001 (CSD), + many others |
| 577 | ClearingInstruction | +4000 (AGENCY), +4001 (PRINCIPAL), +4002 (ELIGIBLE_FOR_CLEARING) |
| 587 | LegSettlType | +38 enums (IMM dates, month/year tenors) |
| 803 | PartySubIDType | +40 Bloomberg-specific sub-IDs (4000–4077) |
| 828 | TrdType | +2000–2003 (swap types) |
| 829 | TrdSubType | +2000–2008 (swap sub-types) |
| 2141 | StrategyType | +20 strategy codes (ASW, BB, BX, etc.) |

Full list: 83 fields carry FIX44 enums not present in EP302. All appended per merge rules.

### 2.5 FIX44 Enums NOT to Carry (known decisions)

| Tag | Name | Reason |
|---|---|---|
| 327 | HaltReason | FIX44 had CHAR enums (I,X,P,D,E,M) on an INT field — do not carry |
| 800 | OrderBookingQty | FIX44 had NOE enums (0,1) — do not carry (different field semantics) |

### 2.6 Tag 47 (Rule80A)

Confirmed FIX44-only (deprecated post-FIX 4.3, not in EP302). Carry as `type="STRING"` with all enums including multi-char values (NC, CL, ST, IN, MP, OF, OT, BU).

### 2.7 FIX44-Only Fields (428)

Carried verbatim. Includes:
- Deprecated FIX tags: 20 (ExecTransType), 47 (Rule80A), 76 (ExecBroker), 109 (ClientID), 204 (CustomerOrFirm), 465 (QuantityType)
- Bloomberg algo fields: ~100 fields (BMOA 6102–6200, Berenberg 7113–7180)
- Custom FixPortal fields: 5020–5975, 6203–6886, 8013–8932, 9009–9906, 10009–10015, 20000–23007

---

## 3. Components

### 3.1 Counts

| Category | Count |
|---|---|
| EP302-only | 130 |
| FIX44-only (true custom) | 4 |
| FIX44-only (group-wrappers) | 87 |
| In both | 37 |
| **Expected merged total** | **~258** |

### 3.2 Structural Note — Group Wrappers

EP302 (Orchestra-generated) renders groups inline within messages. FIX44 wraps groups in component definitions (e.g. component `Parties` contains group `NoPartyIDs`).

Of the 87 FIX44 group-wrappers:
- **75** have equivalent inline groups in EP302 — structurally redundant but needed for backward compatibility
- **12** are FixPortal-custom groups not in EP302 — must be carried verbatim

> [!important] Decision Required
> How to handle the 75 redundant group-wrappers. Options:
> 1. **Keep wrapper components** — carry FIX44 wrapper components so existing code referencing them by name still works. Update group contents to EP302's expanded field lists.
> 2. **Drop wrappers, use inline only** — EP302 style. Simpler but breaks any code referencing wrapper component names.
>
> **Recommendation:** Option 1 for the wrappers used in FixPortal messages (Parties, PreAllocGrp, Stipulations, UndInstrmtGrp, etc.). Drop unused wrappers.

### 3.3 FIX44-Only: True Custom Components (4)

| Component | Description |
|---|---|
| `Algos_BMOA` | Bloomberg algo parameters |
| `Algos_Berenberg` | Berenberg algo parameters |
| `HedgeData` | FixPortal hedge fields |
| `Interlisted_Settlement` | FixPortal interlisted settlement |

### 3.4 FIX44-Only: FixPortal-custom Group Wrappers (12)

| Component | Group |
|---|---|
| `ClientCustomDataGrp` | NoClientCustomData |
| `CompDealerQuoteGrp` | NoCompDealerQuotes |
| `DealerGrp` | NoDealers |
| `ExecRuleNameGrp` | NoExecRuleNames |
| `LegRefPriceGrp` | NoLegRefPrices |
| `Notes` | NoNotes |
| `PartyAltIDGrp` | NoPartyAltIDs |
| `PartyAltSubGrp` | NoPartyAltSubIDs |
| `RefPriceGrp` | NoRefPrices |
| `ReferenceIDGrp` | NoReferenceIds |
| `RegulatorTradeIDGrp` | NoRegulatorTradeIDs |
| `UnderlyingReinvCouponGrp` | NoUnderlyingReinvCoupon |

> [!warning] PartyAltIDGrp / PartyAltSubGrp
> In our previous merge we retired these in favour of the FIX50SP2 standard `PartyAltIDs` / `AltPtysSubGrp` components. The same decision should apply here — use EP302 standard components, rename FIX44 custom tags (22086–22091) if still needed, or drop.

### 3.5 Overlapping Components — FIX44 Additions (15 of 37)

| Component | FIX44 Additions |
|---|---|
| `CommissionData` | `CommissionMode`, `CommissionRate`, `CommissionValueType` |
| `FinancingDetails` | `DaysToTerm`, `FinancingContractualDefinitionGrp`, `FinancingContractualMatrixGrp` |
| `Instrument` | 11 fields + 5 component refs (see below) |
| `InstrumentExtension` | `AttrbGrp` |
| `InstrumentLeg` | 4 fields + 4 component refs |
| `LegFinancingDetails` | 2 component refs |
| `LegOptionExerciseDates` | `LegOptionExerciseDeliveryDate` |
| `OptionExerciseDates` | `OptionExerciseDeliveryDate` |
| `OptionExerciseExpiration` | `OptionExerciseExpirationDateGrp` |
| `OrderQtyData` | `ConstantQtyType`, `OrderQtyUnits` |
| `SettlInstructionsData` | `DlvyInstGrp` |
| `SpreadOrBenchmarkCurveData` | `BenchmarkSecurityDesc`, `BenchmarkSecurityType`, `BenchmarkYield` |
| `UnderlyingInstrument` | 5 fields + 6 component refs |
| `YieldData` | `GrossYield` |
| `FloatingRateIndex` | `FloatingRateIndex` (field name = component name — potential collision) |

All appended as `required="N"`.

### 3.6 Required Attribute Changes (EP302 wins)

| Component | Field | FIX44 | EP302 |
|---|---|---|---|
| Instrument | Symbol | Y | **N** |
| UnderlyingInstrument | UnderlyingSymbol | Y | **N** |

---

## 4. Messages

### 4.1 Counts

| Category | Count |
|---|---|
| EP302 app messages | 173 |
| FIX44 admin (excluded) | 7 (+Heartbeat = 8 entries) |
| FIX44 non-admin | 80 |
| EP302-only | 93 |
| FIX44-only non-admin | 0 |
| Overlapping | 80 |
| **Expected merged total** | **173** |

All 80 FIX44 non-admin messages have matching msgtypes in EP302. No FIX44-only messages to carry.

### 4.2 Admin Messages Excluded

Heartbeat (0), TestRequest (1), ResendRequest (2), Reject (3), SequenceReset (4), Logout (5), Logon (A), XMLnonFIX (n)

### 4.3 Name Differences (EP302 wins)

| MsgType | FIX44 Name | EP302 Name |
|---|---|---|
| 6 | IndicationOfInterest | IOI |
| b | MassQuoteAcknowledgement | MassQuoteAck |
| BC | NetworkStatusRequest | NetworkCounterpartySystemStatusRequest |
| BD | NetworkStatusResponse | NetworkCounterpartySystemStatusResponse |

### 4.4 OrigClOrdID `required=` Status

| Message | EP302 | FIX44 | Decision |
|---|---|---|---|
| OrderCancelRequest (F) | N | Y | **Override to Y** (FIX44 standard, not a FixPortal change) |
| OrderCancelReject (9) | N | Y | **Keep N** (EP302 wins, no override) |
| OrderCancelReplaceRequest (G) | N | Y | **Keep N** (EP302 wins) |

### 4.5 FixPortal additions — Critical Messages

#### NewOrderSingle (D) — 22 FixPortal additions

Fields: `LocateBroker`, `LocateIdentifier`, `NumDaysInterest`, `CustomerOrFirm`, `TrdType`, `TrdSubType`, `SecondaryTrdType`, `TradeContinuation`, `Tenor`, `ApplyRestriction`, `StagedOrderIsInquiry`, `Ccy1MarketType`, `Ccy2MarketType`, `AutoOrdType`, `AllocationType`, `FixingDate`, `BTOrderInst`, `RegulationID`, `RetailOrder`, `DisplayInst`, `Account_BMO`, `Routable`, `OrderAttributeTypes`
Components: `ExecRuleNameGrp`, `Notes`, `Algos_Berenberg`, `Algos_BMOA`

#### ExecutionReport (8) — ~100+ FixPortal additions

Largest message. FixPortal fields span trade IDs, pricing/hedge, convertible bonds, regulatory/clearing, settlement/currency, FI-specific, auction, book/alloc, display, swap/FX, client pricing, basket, and custom fields. Plus FixPortal components: `ClrInstGrp`, `PositionAmountData`, `QuotQualGrp`, `RelatedInstrumentGrp`, `ClientCustomDataGrp`, `CompDealerQuoteGrp`, `HedgeData`, `RefPriceGrp`, `ReferenceIDGrp`, `PriceQualifierGrp`, `Notes`, `Interlisted_Settlement`.

#### OrderCancelRequest (F) — 4 FixPortal additions

Fields: `LocateBroker`, `LocateIdentifier`, `ApplyRestriction`, `BTOrderInst`
Component: `Stipulations` (not in EP302 for this message — treat as FixPortal addition)

#### OrderCancelReplaceRequest (G) — 16 FixPortal additions

Fields: `LocateBroker`, `LocateIdentifier`, `Rule80A`, `CustomerOrFirm`, `StrikeTime`, `TrdType`, `TrdSubType`, `SecondaryTrdType`, `TradeContinuation`, `SpottingProcess`, `CrossIndicator`, `Tenor`, `ApplyRestriction`, `ValidSeconds`, `DueInSeconds`, `StagedOrderIsInquiry`, `AutoConfirm`, `AutoOrdType`, `BTOrderInst`
Components: `ExecRuleNameGrp`, `Notes`

#### OrderCancelReject (9) — 7 FixPortal additions

Fields: `Side`, `ExecID`, `NOE`, `StrikePrice`, `ExpireDate`, `ExpireTime`, `PositionEffect`

> [!note] NOE Reference
> The FixPortal addition `NOE` at tag 800 must be referenced as `OrderBookingQty` (the EP302 name) in the merged dictionary.

### 4.6 Other Required Attribute Conflicts (EP302 wins)

| Message | Field | FIX44 | EP302 |
|---|---|---|---|
| ExecutionReport (8) | AvgPx | Y | **N** |
| OrderCancelRequest (F) | OrderQtyData | Y | **N** |
| OrderCancelReplaceRequest (G) | OrderID | Y | **N** |

### 4.7 Component vs Inline Group Mapping

FIX44 uses named components where EP302 inlines the group directly:

| FIX44 Component | EP302 Inline Group | Used In |
|---|---|---|
| Parties | NoPartyIDs | D, F, G, 8, H, many others |
| PreAllocGrp | NoAllocs | D, G |
| Stipulations | NoStipulations | D, G, F |
| UndInstrmtGrp | NoUnderlyings | D, G, H, 8 |
| StrategyParametersGrp | NoStrategyParameters | D, G, 8 |
| OrderAttributeGrp | NoOrderAttributes | D, G, 8 |

> [!important] Decision Required
> Same as Section 3.2 — keep wrapper components or use inline groups?

---

## 5. Items Requiring Decision

| # | Item | Options | Recommendation |
|---|---|---|---|
| 1 | Group wrapper components (75 redundant) | Keep wrappers / Drop and use inline | Keep wrappers referenced by FixPortal messages; drop unused ones |
| 2 | PartyAltIDGrp / PartyAltSubGrp (tags 22086–22091) | Keep with rename / Retire in favour of EP302 standard | Retire — use EP302 `PartyAltIDs` / `AltPtysSubGrp` (as per previous decision) |
| 3 | FloatingRateIndex field/component name collision | Verify QuickFIX/n handles it | Investigate before merge |
| 4 | Tag 2174 LegIndexAnnexDate (FIX44 type PERCENTAGE) | FIX44 bug? | Use EP302 LOCALMKTDATE |

---

## 6. Items Already Decided (from prior work)

| Item | Decision |
|---|---|
| Tag 47 Rule80A | Carry from FIX44 as STRING |
| Tag 327 HaltReason | EP302 INT, no FIX44 CHAR enums |
| Tag 604 NoLegSecurityAltID | EP302 NUMINGROUP |
| Tag 800 OrderBookingQty | EP302 QTY, not FIX44 NOE |
| Tags 1506, 1586–1605 | EP302 post-EP definitions (no collisions) |
| OrderCancelRequest (F) OrigClOrdID | required="Y" (override) |
| OrderCancelReject (9) OrigClOrdID | required="N" (EP302 wins) |
| Symbol / UnderlyingSymbol required | N (EP302 wins) |
