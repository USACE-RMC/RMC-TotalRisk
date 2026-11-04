# RMC-TotalRisk UI and App roadmap (v2.0.0-beta.1)

**Target:** `v2.0.0-beta.1` around 2027-05-07. Week 1 is the week of 2026-11-09, and weeks 3 and 7–8 carry holiday buffers.

- Specification: [UI_ARCHITECTURE.md](../requirements/UI_ARCHITECTURE.md).
- Completion checklist: [DEFINITION_OF_DONE.md](DEFINITION_OF_DONE.md).
- Status: [UI_PROGRESS.md](UI_PROGRESS.md).

## Ratified decisions (2026-11-04)

1. The UI library holds no WPF controls. Elements own the core OxyPlot `PlotModel`, and every view lives in the App.
2. v1 `.tra` input import is in beta.1 scope. Results are re-run.
3. The `.tra` extension is retained. A v1 file is imported into a new project and Save As is forced. A v1-schema file is never written in place.
4. Collection-level undo is built upstream in wpf-framework.
5. RMC-BestFit is kept at parity: every applicable improvement is backported in the same cycle, with no regressions and with backward compatibility. UI machinery both apps need lives in wpf-framework.
6. BestFit references move to `Id`-based references, and older files still resolve by name.
7. Agent guidance files are never tracked. Each README carries an AI Disclosure section.
8. `.bestfit` import is in beta.1 scope.
9. Localization is key-first in TotalRisk from the start. The UI language and the number format are separate settings.

## Tracks

| Track | Repository | Scope |
|---|---|---|
| S0 | TotalRisk, audit of BestFit and framework | Untrack agent guidance files; add the AI Disclosure |
| F0 Foundation | TotalRisk | Four projects; registration seam; element base; persistence; resolver; undo layers; test harnesses and audits; validator and CI; shell skeleton; package-compatibility proof |
| FW | wpf-framework | FW-1 collection undo; FW-2 shared helpers and a test-support package; FW-3 language infrastructure; fixes from [FRAMEWORK_ISSUES.md](FRAMEWORK_ISSUES.md) |
| BF | RMC-BestFit | Standing parity track over [BESTFIT_ISSUES.md](BESTFIT_ISSUES.md) |
| H / T / R / C | TotalRisk | Hazard, Transform, Response and Consequence functions |
| ET / FT | TotalRisk | Event-tree and fault-tree editors |
| IMP | TotalRisk | v1 `.tra` import (both schema generations) |
| BFI | TotalRisk | `.bestfit` import |
| RA | TotalRisk | Risk analysis: diagram, options, run, results, advanced analyses |
| CB | TotalRisk | Cost-benefit study (gated on the serialized study in the model library) |
| L10N | wpf-framework, then both apps | Languages, glossary and native review |

## Function scope

| Category | Cycle A (v1 parity) | Cycle B (v2 additions and BestFit import) |
|---|---|---|
| Hazard | Tabular, Parametric, Nonparametric, Composite | Bivariate, extrapolation policy, epistemic mixture; BestFit univariate/B17C/point-process → Parametric, mixture/composite → Composite, bivariate → Bivariate |
| Transform | Tabular, Linear, Power | Composite, Bivariate; BestFit rating curve → uncertain Tabular |
| Response | Tabular, Parametric, Bivariate (collapse), Composite | Bivariate joint, Deteriorating, epistemic mixture, tree-source chains; BestFit fits → Parametric |
| Consequence | Tabular, Composite | Parametric, Bivariate, epistemic mixture |
| Trees | Event tree, templates, node importance | Fault tree, common-cause groups, shared limbs, importance |

RFA and LifeSim imports follow beta.1.

## Cycle protocol

Every feature set runs four steps:

1. **Mockup.** An HTML mockup is approved before any XAML is written.
2. **Build** (one week). Build to the Definition of Done, write the human test script, and do the BestFit backport.
3. **Test** (one week). Findings go in `feedback/<track>-<cycle>.md`, using [the template](feedback/TEMPLATE.md).
4. **Fix** (at most one week). Each bug first becomes a failing test.

Tracks are staggered so that one or two categories are in their test week at any time.

## Schedule

| Weeks | Parallel work |
|---|---|
| 1–2 | S0 ∥ F0 (blocks the function tracks) ∥ FW-1 ∥ BF-1 ∥ M0 mockups (shell, explorer, common editor template, palette, plot conventions, options dialog) |
| 3–8 | Cycle A: H and T from week 3, R and C from week 4 ∥ ET-A from week 5 ∥ IMP core ∥ FW-2 ∥ BF backports |
| 9–14 | Cycle B for H, T, R and C with the BFI mappers ∥ ET-B and FT ∥ IMP per-type mappers ∥ FW-3 ∥ BF `Id` references and string extraction ∥ RA-1 mockups |
| 15–20 | RA-1 (diagram, options, run) → RA-2 (results views) → RA-3 (advanced analyses) ∥ feedback backlog ∥ BF `PlotModel` migration |
| 19–23 | CB-1 and CB-2, gated on the serialized cost-benefit study landing by about week 16 ∥ translations and native review |
| 23–26 | Integration hardening: full v1 and BestFit corpus import, cross-reference and delete scenarios, theme and language sweeps, installer and update channel → beta.1, alongside a BestFit parity release |

## Risks

- **Model and UI work compete for the same weeks.** Model work (the serialized cost-benefit study and the exhaustive testing campaign) runs in the same window. UI tracks never edit `src/RMC.TotalRisk`. A model gap becomes a model-library request.
- **One `RMC.Numerics` version must resolve across all consumers:** the model library, wpf-framework (`[2.1.4,3.0.0)`) and the RMC.BestFit package.
- **The CWBI release allowlist must keep excluding the UI and App projects.**
