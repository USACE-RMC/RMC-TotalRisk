# RMC-TotalRisk UI and App Architecture

**Version 0.1 (2026-11-04).** This is the normative specification for the desktop layers of RMC-TotalRisk v2.0:
`RMC.TotalRisk.UI`, `RMC.TotalRisk.UI.Tests`, `RMC.TotalRisk.App` and `RMC.TotalRisk.App.Tests`. The model library rules in [MODEL_LIBRARY_ARCHITECTURE.md](MODEL_LIBRARY_ARCHITECTURE.md) are unchanged. In particular, `RMC.TotalRisk.dll` still references only `RMC.Numerics`.

## 1. Layering

```
RMC.Numerics ← RMC.TotalRisk          net10.0; headless model library (unchanged)
             ← RMC.TotalRisk.UI       net10.0-windows; project, elements, persistence, import, plot models
             ← RMC.TotalRisk.App      net10.0-windows WinExe; every XAML view, the shell controller
Tests:  RMC.TotalRisk.UI.Tests        MSTest.Sdk; headless by default
        RMC.TotalRisk.App.Tests       MSTest.Sdk; STA host with the production theme dictionaries
```

**References.**

| Project | May reference | Must never reference |
|---|---|---|
| `RMC.TotalRisk.UI` | the model library; `RMC.Wpf.Framework.Core`; `RMC.Wpf.Framework.Models` (SQLite, the OxyPlot core `PlotModel`); the `RMC.BestFit` model package (for `.bestfit` import only) | WPF control assemblies (`PresentationFramework` controls, `OxyPlot.Wpf`, `Framework.Controls`) |
| `RMC.TotalRisk.App` | everything above, plus `RMC.Wpf.Framework.Controls` and `RMC.Wpf.Framework.Support` | — |

**Why no WPF controls in the UI library.** In RMC-BestFit, the UI-library elements own live `OxyPlot.Wpf.Plot` controls, so every element test has to run on an STA thread. TotalRisk elements own the core `PlotModel` and plot-settings state. Views live only in the App. As a result, UI.Tests run headless, and STA is needed only for App.Tests.

**Packages.**
- The framework is consumed as the `RMC.Wpf.Framework.*` NuGet packages through central package management.
- Every project commits its `packages.lock.json`.
- One `RMC.Numerics` version must resolve across the model library, the framework and the RMC.BestFit package. Foundation work proves this before any element work starts.
- The CWBI release allowlist never ships the UI or App projects.

## 2. Project model

- **`TotalRiskProject : ProjectBase`.**
  - No process-wide singleton is required for compute or tests. Tests construct private instances.
  - Collections, in explorer order:
    1. Hazards
    2. Transforms
    3. System Responses
    4. Consequences
    5. Risk Analyses
    6. Cost-Benefit Studies
  - Dirty aggregation subscribes to every collection exactly once per project lifetime. RMC-BestFit's double subscription is the trap to avoid.
- **`ProjectFunctionResolver : IRiskFunctionResolver`.**
  - Resolves function references by `IRiskFunction.Id` across the four function collections.
  - Names are a lenient fallback only.
  - Composite functions, tree sources and risk-analysis graphs all resolve through it.
- **Delete.** Deleting a function that something references is blocked, with a message that lists the referrers. There is an explicit "delete and detach" option only where the model supports detaching.

## 3. Elements

### 3.1 Base type

`RiskFunctionElement<TFunction> : ElementBase` wraps exactly one model function and owns:

- **Content undo.** Every model mutation records a before/after `ToXElement()` snapshot. Undo and redo reconstruct the function through `RiskFunctionFactory` and preserve its `Id`.
- **Property undo.** `RecordPropertyChange` for element metadata and display settings.
- **An edit-suspension scope.** It disables recording during `Open`, `Copy` and bulk loads, and clears history afterwards.
- **Validation sync.** Model `Validate()` messages become framework messages with **stable codes and property paths** (§5).
- **Plot models.** One `PlotModel` per view surface, built from `ComputeUncertaintyResults` and the function's evaluation API. Plot settings are serialized with the framework plot serializer and are undoable.
- **Copy.** It assigns a fresh `Id`.

### 3.2 One element class per function type

Example names: `TabularHazardElement`, `ParametricHazardElement`, `LinearTransformElement`, `EventTreeResponseElement`, `TabularConsequenceElement`. There is one collection class per category.

### 3.3 Registration seam

Each function type contributes one `XxxRegistration` file, implementing `IElementRegistration`, that declares:

- the display name, explorer icon key and menu entry;
- the element factory;
- the persistence table name and columns;
- the v1 `.tra` mapper (where the type existed in v1);
- the `.bestfit` mapper (where BestFit produces the type).

The App adds a matching `IViewRegistration` that maps the element type to its document and properties view factories. The shell controller resolves views through these maps. Long type-check chains are not allowed.

Adding a function type therefore adds files only. Changes to shared files (project, controller, theme and string dictionaries) are coordinated, one writer at a time.

## 4. Persistence

- **One SQLite file per project, with the `.tra` extension.**
- **Tables.**
  - `Project`: metadata, software version, layouts, project label lists.
  - One ordering table per collection.
  - One table per element type.
- **Function state.**
  - Stored as `ToXElement()` XML in `RiskSerializationMode.ByReference` mode.
  - Open runs in two passes: every function is read first, then references are re-attached through `ProjectFunctionResolver`.
- **Plot settings** are XML columns. **Results** are compressed JSON (`ToJson`/`FromJson`). BinaryFormatter is never used.
- **Schema evolution.**
  - Missing columns are added.
  - A column whose type has changed is migrated with a copy. It is never dropped and re-added.
  - A failed load surfaces a diagnostic and leaves the element visible with the error. It is never silently cleared.
- **Generation detection.**
  - v1 files are recognized by their schema (two v1 generations exist) and `SoftwareVersion`.
  - Opening one imports it into a new in-memory v2 project and forces Save As to a new path.
  - A v1-schema file is never written in place.
- **Identity checks.** After save → close → reopen, every function's `ToXElement()` and `CanonicalHash()` must be identical.

## 5. Validation messages

- Codes have the form `<TYPE>-<E|W>-<nnn>`. They are stable per message source: the same model message always maps to the same code, and they are never renumbered on re-sync.
- Every message carries the property path the model reports (`ValidationIssue.ObjectPath`) or the element property that produced it, so the view can highlight the field.
- Severity follows the model contract: `Error:` invalidates the element, `Warning:` is advisory.

## 6. Undo

| Layer | Mechanism | Scope |
|---|---|---|
| Property | `RecordPropertyChange` (500 ms merge window) | element |
| Function content | XML-clone snapshot action | element |
| Tables | snapshot action around paste, row insert/delete and cell commit | element |
| Plot | `PlotModel` undo manager (series, axes, annotations, legend) | element |
| Collection | add, delete, duplicate, move, sort, rename, paste, copy-from-project | collection, routed by the shell when the explorer has focus (framework feature) |

Every user-visible edit produces exactly one undo entry. Undo restores the prior XML bit-exactly.

## 7. Views (App)

- Each function type has a **document view** (plots plus tabular results) and a **properties view** (inputs). This follows the v1 and BestFit convention, using the shared editor template approved at the M0 mockup gate.
- Views bind to the element. Code-behind is limited to view concerns.
- All colors and brushes come from theme resources (`DynamicResource`). Hard-coded colors are rejected by a source audit.
- All user-visible text comes from `Strings.*` resource keys. Literal text is rejected by a source audit (§8).
- Every view must load in Light, Dark and Blue with zero WPF binding errors.

## 8. Localization

- **UI language** is a runtime setting served by the framework language service, which mirrors the theme service. Strings live in per-library `Strings.*` resource dictionaries.
- **Number format** is a separate setting. The UI displays and parses numbers in the chosen number culture. The model, the files and serialization stay `CultureInfo.InvariantCulture`.
- **Translations** use a domain glossary for dam and levee risk terms and get a native-speaker review before shipping. A pseudo-locale (long, accented strings) checks layouts for clipping.

## 9. Import

- **`Import/V1`** reads both v1 `.tra` schema generations. It maps each v1 function to its v2 model type, using the v1 values verbatim. Results are not imported; analyses are re-run.
- **`Import/BestFit`** reads `.bestfit` analyses through the RMC.BestFit model types and maps them to model functions:

  | BestFit source | TotalRisk function |
  |---|---|
  | univariate fits | `ParametricUnivariateHazard` with an injected posterior |
  | mixture and composite fits | `CompositeHazard` |
  | bivariate fits | `BivariateHazard` |
  | rating curves | uncertain `TabularTransform` |
  | distribution fits used as fragility | `ParametricResponse` |

- Every importer has a fixture test, and the committed example files form a regression corpus.

## 10. Testing contract

- **UI.Tests**
  - Lifecycle.
  - Persistence-hash round trips.
  - Exactly-one-entry undo for every edit kind.
  - A fixed-seed undo walk (N random edits, undo all, redo all).
  - The validation matrix.
  - Import fixtures.
  - Each persistence test uses a private project and a temporary file.
- **App.Tests**
  - A per-view STA smoke test in all three themes, with binding errors captured and treated as failures.
  - Color and literal-string source audits.
  - Registration resolution.
  - The pseudo-locale layout run.
- **Gates**
  - Zero warnings and full XML documentation in both new libraries.
  - Every suite runs in the fast gate and in CI.
- The full checklist is [../ui/DEFINITION_OF_DONE.md](../ui/DEFINITION_OF_DONE.md).

## Version history

- **v0.1 (2026-11-04):** initial specification. Ratified decisions:
  - no WPF controls in the UI library;
  - v1 input import and BestFit import are in beta.1 scope;
  - `.tra` is retained with generation detection;
  - collection undo is built in the framework;
  - BestFit parity is maintained throughout;
  - localization is key-first.
