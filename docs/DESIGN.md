# XafMergerTool: design (2026-09-09)

## The question

Can a running XAF Blazor app write a view's user-layer customisations back into the module's
`Model.DesignedDiffs.xafml`, so they become part of the source?

## Scope

- Sample module: `Customer` and `Order`, one DetailView worth rearranging.
- One action, **Merge To Module**, on DetailView and ListView. Visible only when
  `Debugger.IsAttached` or `XafModelMerge:Enabled` is true in appsettings.
- Per view only. No whole-model merge, no navigation/actions/localisation, no conflict handling
  (last write wins), no WinForms.

## Mechanics (from DevExpress 26.1 source, `DevExpress.ExpressApp`)

1. The user layer is `((ModelApplicationBase)Application.Model).LastLayer`, Id `UserDiff`
   (`XafApplication.LoadUserDifferences`). `Application.SaveModelChanges()` persists it via
   `ModelDifferenceDbStore.SaveDifference`, which is `new ModelXmlWriter().WriteToString(layer, aspect)`.
2. Merge serialises the whole user layer with the same writer, takes `/Application/Views/*[@Id=viewId]`
   from it, and merges that element into the module xafml node by node with `System.Xml.Linq`,
   mirroring `ModelNode.MoveNode` (state table and deviations in `MERGE-003-PLAN.md`).
3. Removing from the user layer is `((ModelNode)View.Model).Undo()`, the same call
   `ResetViewSettingsController` makes. Then `SaveModelChanges()`.
4. Trap: `SaveDifference` skips an aspect whose XML serialises to empty, so an emptied user layer
   would leave stale XML in `ModelDifferenceAspect`. The action writes
   `ModelDifferenceDbStore.EmptyXafml` into the row itself in that case.

## Guards from the Codex source review (2026-09-09)

- Refuse when a localized aspect (index > 0) also holds a diff for the view: `Undo()` clears all aspects.
- Refuse when the view element itself carries `IsNewNode`: `Undo()` leaves that stub behind
  (`ResetViewSettingsController` disables itself for the same case).
- Carry the module element's `IsNewNode` onto the replacement, or the module's own view vanishes.
- `ClearEmptyUserAspects` honours `SaveDifference`'s `Version` guard.
- Insertion order is Index-first, then Id, like `DoSortNodesByDefault`.
- Descendant markers (MERGE-003): resolved by the node-by-node merge in `docs/MERGE-003-PLAN.md`.

## Acceptance

- Playwright (C#, NUnit): log in, open the Customer DetailView, move a field via Customize Layout,
  Merge To Module, restart the app, assert field position via DOM, assert the module xafml contains
  the layout node, assert `ModelDifferenceAspect` no longer does.
- Manual, recorded in README: the merged module xafml opens in the Model Editor without being rewritten.

## Deliberate limitation

Works only on a developer machine with the source checked out. It is the missing "save to source"
button for the designer the app already has, not runtime editing.
