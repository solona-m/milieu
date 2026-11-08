# Architecture Decision Records

One file per significant, hard-to-reverse decision (a chosen technology,
a boundary, a data model, a security model). The agents read these:

- The **planner** reads relevant ADRs before drafting and flags any plan that
  contradicts one.
- The **plan-critic** checks the plan for consistency against them.
- The **code-critic** reads them, lists which it read, and flags any diff that
  contradicts an ADR as `FAIL`.

Keep them short and durable. A common lightweight format:

```markdown
# ADR NNNN: [Title]

## Status
Proposed | Accepted | Superseded by ADR-XXXX

## Context
[The forces at play — technical, product, organizational — that make this a
real decision.]

## Decision
[What we decided, stated plainly.]

## Consequences
[What becomes easier and what becomes harder as a result.]
```

## Index

- [0001](0001-record-architecture-decisions.md) Record architecture decisions
- [0002](0002-penumbra-temporary-mod-delivery.md) Generated files reach the game through a Penumbra temporary mod
- [0003](0003-code-authored-dome-avfx.md) The dome VFX is authored in code, modelled on the reference mod
- [0004](0004-bundled-backdrops-as-release-assets.md) Bundled backdrops are pinned release assets, downloaded on demand
- [0005](0005-pre-converted-bundled-backdrops.md) Bundled scenes ship pre-converted, published by a guarded local script
- [0006](0006-synced-delivery-through-an-item-vfx.md) Synced delivery through an item's VFX
