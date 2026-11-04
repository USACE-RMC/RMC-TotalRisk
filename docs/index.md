# RMC-TotalRisk Documentation Map

Development documentation for the v1.1 effort. The v1.0 end-user documentation (User's Guide, Technical Reference Manual, Verification Report) is linked from the repository [README](../README.md).

| Document | Purpose |
|---|---|
| [ROADMAP.md](ROADMAP.md) | The phased development roadmap — single source of truth for phases and exit gates |
| [PROGRESS.md](PROGRESS.md) | Per-session progress log (newest first) — read this first when resuming work |
| [verification.md](verification.md) | Legacy-oracle conversion strategy + Monte Carlo tolerance policy |
| [verification/](verification/README.md) | Per-family verification results (the living v1.1 counterpart of the 2024 Word report) |
| [references.md](references.md) | Consolidated IEEE-numbered bibliography |
| [reports/](reports/) | Source reports and technical notes the technical reference builds on: the 2022 technical report draft, the 2024 verification report, the four 2026 technical notes (risk definitions, failure-mode combination, system risk, uncertainty analysis), and the mixture/competing-risks overviews |
| [requirements/MODEL_LIBRARY_ARCHITECTURE.md](requirements/MODEL_LIBRARY_ARCHITECTURE.md) | Normative model-library architecture spec (layout, contracts, seeding, sampling, clusters, engine) |
| [requirements/EVENT_AND_FAULT_TREE_RESPONSE_DESIGN.md](requirements/EVENT_AND_FAULT_TREE_RESPONSE_DESIGN.md) | Normative tree-response design, fully implemented (event trees and exact static fault trees): conditional-fragility boundary, math, references, authoring, LHS, tests, and performance |
| [requirements/BIVARIATE_RISK_DESIGN.md](requirements/BIVARIATE_RISK_DESIGN.md) | Normative bivariate risk-analysis design, fully implemented and verified: copula-linked bivariate hazard, two-way-table transform/consequence, the BivariateResponse port + joint mode, two-port graph, conditional-bin engine integration, verification program |
| [requirements/SHARED_FUNCTIONS_STRATEGY.md](requirements/SHARED_FUNCTIONS_STRATEGY.md) | Cross-repo strategy: shared function math in Numerics; BestFit import contract |
| [technical-reference/](technical-reference/README.md) | Per-family math documentation: input-function families, system components and the risk-element graph, the risk analysis engine, failure-mode combination, uncertainty analysis, integration and loss-exceedance construction, composites, cascades, event trees, fault trees, sensitivity, value of information, hashing/seeding, and the results catalog |
| verification-requests/ | Specs for user-executed reference runs that produce committed benchmark data (created as needed) |
