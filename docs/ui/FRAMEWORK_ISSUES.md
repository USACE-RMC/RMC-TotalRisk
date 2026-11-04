# wpf-framework issue log

These are issues in `C:\GIT\wpf-framework` (v1.0.5) found while building the RMC-TotalRisk UI. Fixes land upstream. TotalRisk and RMC-BestFit then consume the published package. Status values: Open, In progress, Fixed (version), Won't fix (reason).

| # | Area | Issue | Location | Status |
|---|---|---|---|---|
| FW-01 | Undo | Element-collection undo is not reachable from the shell. `ElementCollectionBase.RecordAddElement`/`RecordRemoveElement` exist, but `MainWindow.GetActiveUndoManager()` returns `null` outside a document (its comment says "Fall back to project-level undo manager"). Explorer add, delete, duplicate, move, sort, rename, paste and copy-from-project are not undoable. `Sort` records nothing at all. | `src/FrameworkInterfaces/Project/ElementCollectionBase.cs:545,563`; `src/FrameworkUI/Main Window/MainWindow.xaml.cs:1959-1975` | Open |
| FW-02 | Undo | The plot undo manager exists only in app/demo code (`FrameworkUI.Demo/Model/Support/PlotUndoManager.cs`, with a copy in BestFit). Promote a `PlotModel`-based version into the framework. | `src/FrameworkUI.Demo/Model/Support/PlotUndoManager.cs` | Open |
| FW-03 | Undo | Grid controls (`CopyPasteDataGrid`, `ValidationDataGrid`, NumericControls table editors) have no undo integration. Paste and row add/delete need a snapshot hook so consuming elements record one entry per edit. | `src/GenericControls/DataGrid/`, `src/NumericControls/Data/` | Open |
| FW-04 | Controls | `ConvertBack` methods and switch defaults throw `NotImplementedException`. | `MathEditorControl.xaml.cs:306,348,401`; `TimeSeriesTable.xaml.cs:830`; `UncertainOrderedDataTableEditor.xaml.cs:515`; DistributionSelector defaults | Open |
| FW-05 | Project | `ElementBaseBuff` duplicates `ElementBase` without undo support, which is a trap for consumers. Remove it or mark it obsolete. | `src/FrameworkInterfaces/Project/ElementBaseBuff.cs` | Open |
| FW-06 | Project | Dirty propagation must be wired by hand in every project (`SubscribeCollectionEvents`). `ProjectBase` should own idempotent subscription. | `src/FrameworkInterfaces/Project/ProjectBase.cs` | Open |
| FW-07 | Shell | App-specific leftovers: `ShellPublicVariables.SoftwareExtension` defaults to `".tra"`; comments reference `BestFitProject.Save()`. | `src/FrameworkUI/Main Window/MainWindow.xaml.cs:~961` | Open |
| FW-08 | Shell | `MainWindow` code-behind is about 2,900 lines. Undo/redo `CanExecute` refreshes button state on every command query. | `src/FrameworkUI/Main Window/MainWindow.xaml.cs` | Open |
| FW-09 | Design | Global statics (`Messenger`, `ThemeService`, `UserSettings`, `ShellPublicVariables` simulation flags) make parallel tests fragile and prevent more than one project instance. | various | Open |
| FW-10 | Testing | There is no shared test-support package. STA dispatcher hosts and theme-loading helpers are private to each test project. Promote `WpfTestHost`, a SQLite persistence test scope, binding-error capture and the color/literal-string audits. | `tests/` | Open |
| FW-11 | Localization | Runtime language switching (issue #24): a `LanguageService` mirroring `ThemeService`, `Strings.*` dictionaries, an Options selector, and extraction of about 1,300 XAML and 200 code strings. | issue #24 | Open |
| FW-12 | Packaging | The `RMC.Numerics [2.1.4,3.0.0)` range must admit the consumers' pinned prerelease (`2.2.0-dev.*`) under central package management and lock files. | `src/Packaging/` | Open |
| FW-13 | Repo | `artifacts/WPF-Framework-public-clean/` is a full duplicate source tree that pollutes searches. The `packages/` folder holds stale 1.0.1 and 1.0.2 packages. | repo root | Open |
