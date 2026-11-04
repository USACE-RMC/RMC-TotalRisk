# RMC-BestFit issue and parity log

These are issues found in `C:\GIT\RMC-BestFit` while using it as the reference for the RMC-TotalRisk UI. **P-xx** rows are improvements TotalRisk introduced that must be backported so the two apps stay at parity.

**Fix rules**
- No regressions: every BestFit suite stays green.
- Backward compatibility: every committed example `.bestfit` reopens with identical analysis XML, and older files keep opening.

Status values: Open, In progress, Fixed (commit), Won't fix (reason).

## Defects and risks

| # | Issue | Location | Status |
|---|---|---|---|
| BF-01 | CI builds and tests only the model library. The UI, App and Api projects and their tests never run in CI, so the zero-warning and XML-doc gates are not enforced there. | `.github/workflows/Integration.yml` | Open |
| BF-02 | Collection events are subscribed twice. The constructor subscribes, and `CreateNew`/`CreateNewDummyProject` subscribe again without unsubscribing, so app startup double-handles every event until the first `Close()`. The test scope repeats the pattern. | `src/RMC.BestFit.UI/BestFitProject.cs:68,597,635`; `UI.Tests/Elements/SQLitePersistenceTestScope.cs` | Open |
| BF-03 | Rename-on-save calls `File.Move` without checking whether the target exists, so an IOException can abort the save half-way. | `BestFitProject.cs:811-815` | Open |
| BF-04 | Schema drift silently loses data. All 13 `CreateTable` copies do `DeleteColumn` + `AddColumn` when a column's type differs. | every element `CreateTable` | Open |
| BF-05 | `FittingAnalysis.Open` returns early on a legacy v1.0 file with no nodes, which skips `SetupBridges`, `SetIsValid`, the estimated-message update and the dirty/notification calls. | `FittingAnalysis` `Open` (~685 vs 770-783) | Open |
| BF-06 | `ValidationMessageAdapter` gives every message an empty property name and renumbers codes from 100 on each sync, so codes are unstable and fields cannot be highlighted. | `src/RMC.BestFit.UI/Elements/Support/ValidationMessageAdapter.cs` | Open |
| BF-07 | `TimeSeriesElement` silently falls back to the `BestFitProject.GetInstance()` singleton. 44 test files touch the singleton or the global `Messenger` while tests run in parallel at method level, and only 31 are `[DoNotParallelize]`. This is a likely source of flaky tests. | `TimeSeriesElement.cs:66`; UI.Tests | Open |
| BF-08 | A "FIX this HACK" on a chain-count mismatch returns silently. | `GUI/Support/Controls/MarkovChainTraceControl.xaml.cs:777` | Open |
| BF-09 | A stale TODO ("undo-redo logic removed pending OxyPlot library updates") contradicts the documented InputData undo migration. | `InputDataControl.xaml.cs:365` | Open |
| BF-10 | The working tree has uncommitted edits: B17C "Quantile Penalty" series retitle, re-enabled `emaCIPoints`/`emaMdPoints`, a solution BOM line, and two example `.bestfit` files. Commit or discard them deliberately. | `B17CAnalysisControl.xaml.cs` and others | Open |
| BF-11 | The `IAnalysisElement.InnerAnalysis` doc says B17C returns null; every element now returns its analysis. | `Elements/Support/IAnalysisElement.cs` | Open |
| BF-12 | Repository guidance is stale: DataContext status, `ReattachSeriesReferences`, the warning count, the Verification-run policy, and the `Copy()` template line references. | local guidance files | Open |
| BF-13 | 15 support XAMLs still use `DataContext="{Binding RelativeSource={RelativeSource Self}}"`, against the DataContext convention. | `GUI/Support/Controls/*.xaml`, `BatchRunWindow.xaml`, `DSSPathSelectorWindow.xaml` | Open |
| BF-14 | The version "2.0.1" is hard-coded in five places. Derive it from one source. | `Directory.Build.props`, `BestFitProject.SoftwareVersion`, `App.ProductVersion`, both `AssemblyInfo.cs` | Open |
| BF-15 | Legacy-version detection is an exact `version == "1.0"` string match. | `BestFitProject` open path | Open |
| BF-16 | `MainProjectNode` maps elements to controls with five type-check chains (about 12 branches each) and has four near-identical batch-run lambdas. Replace them with registration maps. | `src/RMC.BestFit.App/GUI/MainProjectNode.cs` | Open |
| BF-17 | Duplication: `CreateTable` ×13, `DeserializePlotSettings` ×10, copy-pasted collection `Add`/`Insert`/`Save`/`Open`, repeated `GetType()==typeof(...)` scans, and `_readOnlyElementCollections` rebuilt four times despite the `AllCollections()` helper. | UI elements and collections | Open |
| BF-18 | Elements of 1.6k–3.3k lines and code-behind up to 3.7k lines, with no view-model seam. App coverage is capped at about 40%. | `InputDataControl.xaml.cs` and others | Open |
| BF-19 | Copy-from-external is disabled everywhere. All 10 analysis types return `CanCopyFromExternal => false`, and `InsertFromExternalProject` is a no-op in at least two collections. | UI elements and collections | Open |
| BF-20 | Undo round-trip tests are missing for FittingAnalysis, B17C, PointProcess, Univariate, Bivariate and RatingCurve, and there are no per-element plot-undo round-trip tests. | `src/RMC.BestFit.UI.Tests` | Open |
| BF-21 | Theme gaps: hard-coded `#FFE41400` (InputData ×6, TimeSeries ×3, ParameterSets), `BorderBrush="Red"` in 11 properties XAMLs, `Brushes.Gray` in code, and startup always forces the Light theme. | App XAML and code-behind | Open |
| BF-22 | Fragile patterns: UI-library elements own WPF `Plot` controls (forcing STA on every element test); 14 `async void` methods; the toolbar `PropertiesCalled` handler is wired in both XAML and code-behind (opens and immediately closes the properties panel); an empty `Placeholder.cs` in UI.Tests. | UI and App | Open |
| BF-23 | Repository clutter (`artifacts/` with nested worktrees and a vendored framework copy, `tmp/`, `TestResults/`, `output/`, `mfput.log`) confuses search tools. | repo root | Open |

## Parity backports (TotalRisk → BestFit)

| # | Improvement | Introduced by | Status |
|---|---|---|---|
| P-01 | Registration maps instead of type-check chains (resolves BF-16) | TotalRisk foundation | Open |
| P-02 | Shared upstream helpers: generic table persistence (resolves BF-04, BF-17), stable-code validation adapter (resolves BF-06), `PlotModel` undo manager, test-support package | wpf-framework helper promotion | Open |
| P-03 | Function and analysis references by `Id`, with a name fallback for older files | TotalRisk foundation | Open |
| P-04 | Collection-level undo from the explorer | wpf-framework collection undo | Open |
| P-05 | UI and App tests in CI | TotalRisk foundation | Open |
| P-06 | `PlotModel`-in-UI migration, one element at a time (resolves BF-22's STA coupling) | TotalRisk foundation | Open |
| P-07 | Key-first localization: string extraction and the literal-string audit | wpf-framework language infrastructure | Open |
| P-08 | Color and binding-error audits in App tests (guards BF-21) | TotalRisk foundation | Open |
| P-09 | Undo-walk and persistence-identity tests per element (closes BF-20) | TotalRisk foundation | Open |
