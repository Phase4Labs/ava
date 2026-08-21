# AVA Stage 2B.4 Promotion — Structural Gate Enforcement

## Runtime switch

`AVA_STRUCTURAL_GATE_MODE` accepts:

- `enforce` — promoted behavior. Structurally invalid scenarios cannot reach TriggerEngine. This is the default after promotion.
- `shadow` — rollback behavior. Stage 2B.4 still evaluates/logs, but TriggerEngine uses the existing raw DB scenario path.

Quality tiers (`PREFERRED` / `SECONDARY`) remain advisory in both modes.

## Enforcement behavior

1. GPT output is parsed and stored exactly as before.
2. `execution_cards.card_json`, raw text, and `execution_card_scenarios` remain raw model/audit records.
3. `ScenarioStructuralValidator` creates a separate normalized executable card.
4. Hard Entry/Stop/T1 structural failures are excluded.
5. Invalid optional T2/runner ordering is repaired only by omitting the invalid later target; no price is invented.
6. TriggerEngine receives the normalized executable card directly and treats it as authoritative, including an empty scenario list / effective `NO_TRADE`.
7. Existing TriggerEngine probability, grade, and entry-presentation logic remains unchanged.

## Fail behavior

If the promoted decision layer throws unexpectedly, the worker logs `STAGE2B4_GATE_FAILED_FAIL_OPEN` and falls back to the pre-existing raw TriggerEngine path.

## Rollback

```powershell
$env:AVA_STRUCTURAL_GATE_MODE="shadow"
```

Restart AVA. Restore enforcement with:

```powershell
$env:AVA_STRUCTURAL_GATE_MODE="enforce"
```

## Expected logging

```text
STAGE2B4_ENFORCE ... raw=TRADE structural=TRADE valid=2/3 preferred=1 secondary=1
TRIG ... STAGE2B4_EXECUTABLE_OVERRIDE ... verdict=TRADE scenarios=2
```

If every model scenario is structurally invalid:

```text
STAGE2B4_ENFORCE ... raw=TRADE structural=NO_TRADE valid=0/3 ...
TRIG ... STAGE2B4_EXECUTABLE_OVERRIDE ... verdict=NO_TRADE scenarios=0
```
