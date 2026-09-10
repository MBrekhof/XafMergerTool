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

## Runtime Model Editor options (2026-09-10)

Plan and evidence: `ME-AT-RUNTIME-PLAN.md`. The decisions, as implemented:

| | Decision | Where |
|---|---|---|
| D1 | An administrator's variant lives in their own user layer until a developer merges and rebuilds. | README limitations |
| D2 | Runtime actions: role `IsAdministrative` or `CanEditModel`. Merge To Module: developer gate unchanged. | `ModelEditingGuard` |
| D3 | Merging a variant also merges the root's `Variants` subtree, and only that; the pruned element is named after the root's own diff element. | `MergeToModuleController` |
| D4 | A user-layer-created view (`IsNewNode`) is merged whole and removed with `IModelNode.Remove()`; module-defined views keep `Undo()`. Other aspects of a user-created view are dropped: they hold only the `CaptionColon` / `RequiredFieldMark` defaults XAF writes when it first shows a new DetailView. | `MergeToModuleController` |
| D5 | Variant Id = `<root>_<caption sanitised to [A-Za-z0-9]>`; a `Default` entry pointing at the root is added the first time. Captions go to the default aspect via `ModelApplicationBase.SetCurrentAspect("")`, which is what the Model Editor writes and the merge reads (the runtime otherwise writes them to the culture aspect). | `SaveAsVariantController` |
| D6 | Delete Variant only for runtime-created variants; when only the auto-added `Default` remains, the `Variants` subtree is undone so nothing is left behind. | `SaveAsVariantController` |
| D7 | Views are re-created in their frame as `ResetViewSettingsController` does: `CreateShortcut`, `SetView(null)`, `ProcessShortcut`. Needed because `ListView` reads `MasterDetailMode` in its constructor (ListView.cs 68/75) and `LoadModel` does not re-read it. A failure re-attaches the previous or root view before it surfaces. Switching between variants goes through the ViewVariants module's `CurrentFrameViewVariantsManager`. | both controllers |

Two things found on the way that the plan did not know: `Frame.Controllers` is a `LightDictionary`
whose non-generic enumerator throws, so it is iterated with a typed `foreach`; and `SaveDifference`
skips aspects that serialise to empty, so after a view is removed from the user layer the stale
aspect row must be cleared explicitly (`UserLayer.ClearEmptyAspects`, the same workaround the merge
already had for its own Undo).
