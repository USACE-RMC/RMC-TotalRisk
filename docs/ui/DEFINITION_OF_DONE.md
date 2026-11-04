# Definition of Done: UI element types

An element type (and its views) is done only when every item below holds. The specification is [UI_ARCHITECTURE.md](../requirements/UI_ARCHITECTURE.md).

## 1. Design
- [ ] An approved mockup covers every editor state (empty, valid, each validation error, uncertainty on/off, each interpolation transform), all three themes, and one pseudo-locale view. The approval link is recorded in [UI_PROGRESS.md](UI_PROGRESS.md).
- [ ] No App XAML for the type existed before the approval date.

## 2. Lifecycle (UI.Tests)
- [ ] Create from the explorer menu and from the registration factory.
- [ ] Rename, including duplicate names, blank names, illegal characters and leading/trailing whitespace.
- [ ] Duplicate assigns a fresh `Id` and keeps the content equal.
- [ ] Delete while referenced is blocked and lists the referrers. Delete while unreferenced succeeds.
- [ ] Copy to and from another project, including composites that reference other functions.
- [ ] Dirty state propagates from element to collection to project, and clears on save.

## 3. Persistence (UI.Tests)
- [ ] Save → close → reopen gives identical `ToXElement()` and identical `CanonicalHash()`.
- [ ] `ByReference` references re-attach by `Id`; the name fallback is covered.
- [ ] Unknown or extra columns are tolerated; columns are never silently dropped.
- [ ] A corrupt cell produces a visible diagnostic, not a silent reset.

## 4. Undo (UI.Tests)
- [ ] Every editable property, table cell commit, paste, row insert/delete and plot setting produces **exactly one** undo entry.
- [ ] Undo restores the prior XML bit-exactly; redo re-applies it bit-exactly.
- [ ] A fixed-seed undo walk passes: 200 random edits → undo all equals the initial XML → redo all equals the final XML.
- [ ] Collection add, delete, duplicate, move and rename are undoable from the explorer.

## 5. Validation (UI.Tests)
- [ ] Every model `Validate()` message for the type surfaces with a stable code and a property path. A matrix test drives each error and warning case.
- [ ] Element validity matches the model's validity.

## 6. Views (App.Tests)
- [ ] Document and properties views open in Light, Dark and Blue with zero WPF binding errors.
- [ ] Source audits pass: no hard-coded colors or brushes, and no literal user-visible strings.
- [ ] The view registration resolves for the element type.
- [ ] The pseudo-locale run shows no clipped labels.

## 7. Import (UI.Tests)
- [ ] v1: the mapped model equals the v1 values for every v1 type, and re-run results match the v1 reference within tolerance where the example provides one.
- [ ] BestFit: for every mapped type, the mapped model equals the source analysis values.

## 8. Gates
- [ ] `dotnet build`: zero warnings.
- [ ] `dotnet test -c Release`: model, Api, UI and App suites all green.
- [ ] `scripts/validate-code-xml-docs.ps1 -Configuration Debug` green, with UI and App included.
- [ ] Model library untouched (or changed only under a recorded ruling), and all eight perf byte gates bit-identical.
- [ ] CI green.

## 9. Human testing
- [ ] A test script exists in `docs/ui/test-scripts/<Type>.md`. It enumerates every input configuration (each option × uncertainty × transform × edge values) with the expected outcome.
- [ ] The test week is complete, and every finding in `docs/ui/feedback/` is closed (fixed with a regression test, ruled, or deferred with a reason).

## 10. BestFit parity
- [ ] Every pattern or fix this type introduced that applies to BestFit has been backported to RMC-BestFit with all BestFit suites green.
- [ ] Every committed example `.bestfit` reopens with identical analysis XML.
- [ ] Newly found BestFit or framework issues are logged in [BESTFIT_ISSUES.md](BESTFIT_ISSUES.md) or [FRAMEWORK_ISSUES.md](FRAMEWORK_ISSUES.md).
