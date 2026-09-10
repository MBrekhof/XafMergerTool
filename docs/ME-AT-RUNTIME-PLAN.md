# Plan: Model Editor options at Blazor runtime (master-detail, view variants)

Status: implemented 2026-09-10 (MERGE-004 ebcdcc9, MERGE-005 c6047c7, MERGE-006 40eb0a4, MERGE-007
f669214, MERGE-008 4d3d057), each reviewed by Codex; D1 and D2 confirmed by the owner the same
day ("rebuild is the way to go"). What the implementation learned beyond this plan is in
`DESIGN.md`, "Runtime Model Editor options". Written after a two-model assessment (Claude + Codex, same day) of the
question "can Merge To Module save a ListView as master-detail, and save a DetailView layout as a
second option?". Both said yes; this plan adds the runtime UI the Model Editor has and the
Blazor runtime lacks, and the two merge changes that make variants round-trip.

Cards: MERGE-004 to MERGE-008 on the board (project XafMergerTool). Implement in that order.

## Goal

Mimic, at Blazor runtime and for an administrator, the Model Editor options on a view node that a
developer would otherwise set by hand:

| Model Editor | Node / property | Runtime equivalent in this plan |
|---|---|---|
| ListView > MasterDetailMode | `IModelListView.MasterDetailMode` | action *Master-Detail* (MERGE-005) |
| ListView > SplitLayout > Direction, ViewsOrder | `IModelSplitLayout.Direction`, `IModelListViewSplitLayout.ViewsOrder` | same action, one item per placement |
| ListView > MasterDetailView | `IModelListView.MasterDetailView` | action *Detail View* (MERGE-005) |
| ListView > SplitLayout > RelativePosition | `IModelSplitLayout.RelativePosition` | already written by the runtime splitter; nothing to add |
| Views > Add DetailView / clone, Generate content | new `DetailView`/`ListView` node, `IsNewNode` | action *Save As Variant* (MERGE-006) |
| View > Variants > Add, Caption, Current | `IModelViewVariants.Variants`, `IModelVariant` | same action; *Delete Variant* for runtime-created ones |
| DetailView > Layout | layout nodes | already there: *Customize Layout* |
| save to `Model.DesignedDiffs.xafml` | | *Merge To Module*, extended (MERGE-007) |

Everything the runtime writes goes into the user layer exactly as today, so *Merge To Module* stays
the single "save to source" step.

Not mimicked, on purpose: column chooser for ListViews (XAF has it), captions/localisation, AllowEdit,
NewItemRow, anything outside the table. Rename or re-order variants: skip, create a new one.

## Facts the design rests on (verified 2026-09-10)

- Blazor supports `MasterDetailMode.ListViewAndDetailView` only with `DxGridListEditor`; in that mode
  in-place editing (`AllowEdit`, `NewItemRowPosition`) is ignored. Placement is `SplitLayout.Direction`
  (Horizontal = beside, Vertical = under) times `ViewsOrder` (ListViewDetailView = list first). Dragging
  the splitter writes `RelativePosition` into the user layer. Docs 113249, 404203, and the
  `IModelListView.MasterDetailMode` / `IModelSplitLayout.Direction` / `IModelListViewSplitLayout.ViewsOrder`
  reference pages, all 26.1. These interfaces live in `DevExpress.ExpressApp`, so the diff is
  platform-agnostic and belongs in the module xafml.
- No runtime UI sets any of these; *Customize Layout* covers DetailView items only (docs 404353).
- View variants: `ViewVariantsModule` (package `DevExpress.ExpressApp.ViewVariantsModule`,
  `builder.Modules.AddViewVariants()`), `ChangeVariantController` shows the *ChangeVariant* action
  when a view's `Variants` node has two or more entries; the selected variant is stored in
  `IModelVariants.Current` with user customisations. Docs 113011, 113315 (Blazor screenshots present).
- Cloning a view at runtime: `ModelNode.AddClonedNode(source, id)` (ModelNode.cs 1362-1381, DX 26.1)
  adds the node to the writable (user) layer and applies the **composed master** node
  (`source.GetMasterRecursive()`) with `ApplyDiffCore`: every value that differs from its default and
  every child, recursively. The clone is therefore a complete, self-standing view node with
  `IsNewNode="True"`, which is exactly what the Model Editor's "add view + generate content" produces.
  DevExpress's own example does this (`xaf-how-to-save-the-currently-opened-view-as-a-new-view-variant-at-runtime`).
- Removing a runtime-created node: `IModelNode.Remove()` → `ModelNode._Delete` (ModelNode.cs 762-790).
  A node that exists only in the last layer passes `CanRemoveNode()` and is physically removed from the
  layer; no `Removed` stub is left. That is the cleanup path for a merged variant. `Undo()` is wrong
  for it (README limitation "views defined in code or a module only").
- Admin: XAF's documented check is the role flags, `user.Roles.Any(r => r.IsAdministrative)` and, for
  model editing specifically, `r.CanEditModel` (docs 403824). `CanEditModel` is the permission XAF's
  own runtime Model Editor honours, so it is the closest mimic.

## Decisions

- **D1. Admin variants live in the admin's user layer only.** Another user does not see a variant
  until a developer runs *Merge To Module* and rebuilds. This is by design: the tool's purpose is
  source, not runtime sharing. If "visible to everyone without a rebuild" is wanted, that is the
  shared model difference store (README "Shared model differences in the database"), a separate card.
- **D2. Two guards, not one.** Runtime editing actions (MERGE-005, MERGE-006): active when the current
  user's role has `IsAdministrative` or `CanEditModel`. *Merge To Module*: unchanged, `Debugger.IsAttached`
  or `XafModelMerge:Enabled`. In production an admin can shape views for themself; only a developer
  can write them to source.
- **D3. Merge To Module on a variant also merges the root view's `Variants` subtree, and only that.**
  The `Variants` node lives on the root view, in the user layer; without it the merged variant is a
  view nobody can reach. Merging the root's whole diff would sweep unrelated changes to the root
  view along, so the controller builds a pruned element `<DetailView Id="root"><Variants>…</Variants></DetailView>`
  and clears just that subtree with `((ModelNode)root.Variants).Undo()`. Per-view semantics
  otherwise unchanged.
- **D4. Runtime-created variants are merged whole and then removed from the user layer;
  module-defined views keep the `Undo()` path.** Decided by the view element's `IsNewNode` in the
  user layer. The refusal for `IsNewNode` views goes away; the "localised aspects" refusal stays.
- **D5. Variant Id = `<rootViewId>_<Caption sanitised to [A-Za-z0-9]>`**, must not exist in
  `Application.Model.Views`. Caption is what the user sees in *ChangeVariant*. When the root view has
  no `Variants` yet, a `Default` entry pointing at the root is added first, per docs 113011, so the
  action lists both.
- **D6. Delete Variant only for runtime-created variants** (node `IsNewNode` in the user layer).
  Deleting a module-defined variant would need a `Removed` marker to survive the merge, which the
  merger supports but the UI question (delete a view other users may have selected as `Current`) is
  not worth answering now.
- **D7. Re-creating the view after a model change.** Blazor cannot re-layout a live ListView when
  `MasterDetailMode` changes; the action saves the model and replaces the frame's view:
  `Frame.SetView(Application.ProcessShortcut(View.CreateShortcut()))` for the same Id, or with the
  variant Id for *Save As Variant*. Root views only (`TargetViewNesting = Root`); nested ListViews are
  laid out by their parent. Verify during MERGE-005 that `ProcessShortcut` rebuilds a root ListView
  with a fresh collection source; fallback is `Application.CreateListView(id, Application.CreateObjectSpace(type), true)`.

## Work items

### MERGE-004: ViewVariants module + admin guard helper (1h)

- `XafMergerTool.Module.csproj`: `<PackageReference Include="DevExpress.ExpressApp.ViewVariantsModule" Version="26.1.*" />`.
  `Module.cs`: `RequiredModuleTypes.Add(typeof(DevExpress.ExpressApp.ViewVariantsModule.ViewVariantsModule))`.
  `Startup.cs`: `.AddViewVariants()` on `builder.Modules`.
- `XafMergerTool.Module/ModelMerge/ModelEditingGuard.cs`: one static,
  `bool CanEditModel(XafApplication app)` = current user's roles any `IsAdministrative || CanEditModel`.
  `Admin` role in `Updater.cs` already has `IsAdministrative`.
- Gate: app builds, starts, Customer views unchanged (no `Variants` node yet, so no *ChangeVariant* action).

### MERGE-005: Part A, ListView master-detail actions (3h)

`XafMergerTool.Blazor.Server/Controllers/ListViewSettingsController.cs`, `ViewController<ListView>`,
`TargetViewNesting = Root`, category Tools, both actions `Active["CanEditModel"]` per MERGE-004.

- `SingleChoiceAction` **MasterDetail**, `ItemType = ItemTypeExtension`, items:
  `Off` → `ListViewOnly`; `Detail Right` → Horizontal + ListViewDetailView; `Detail Below` → Vertical +
  ListViewDetailView; `Detail Left` → Horizontal + DetailViewListView; `Detail Above` → Vertical +
  DetailViewListView. `SelectedItem` reflects the current model on activation.
- `SingleChoiceAction` **DetailView**, items = `Application.Model.Views.OfType<IModelDetailView>()`
  whose `ModelClass` is the ListView's class (variants included, which is how a compact detail view
  pairs with a split list), sets `MasterDetailView`. Hidden while `MasterDetailMode == ListViewOnly`.
- Execute: set the properties on `(IModelListView)View.Model` (writes to the user layer),
  `Application.SaveModelChanges()`, then D7.
- Nothing changes in the merger: the diff is `<ListView Id=… MasterDetailMode="ListViewAndDetailView"><SplitLayout Direction="Vertical"/></ListView>`,
  keyless child nodes are already keyed by name.
- Gate: manual: Customer ListView > Tools > Master-Detail > Detail Below renders the split; *Merge To
  Module* writes the two attributes into the module xafml; rebuild, restart, split still there,
  `ModelDifferenceAspect` row has no `Customer_ListView`. Automated in MERGE-008.

### MERGE-006: Part B, Save As Variant / Delete Variant (4h)

`XafMergerTool.Blazor.Server/Controllers/SaveAsVariantController.cs`, `ViewController<ObjectView>`,
`TargetViewNesting = Root`, `Active["CanEditModel"]`.

- `PopupWindowShowAction` **SaveAsVariant** with a non-persistent parameter object `{ Caption }`
  (the DX example's shape). On accept:
  1. `root` = the view whose `Variants` contains an entry with `View.Id == View.Model.Id`, else
     `View.Model` itself (search `Application.Model.Views.OfType<IModelViewVariants>()`; no engine API).
  2. `newId` per D5; refuse if it exists.
  3. `clone = ((ModelNode)Application.Model.Views).AddClonedNode((ModelNode)View.Model, newId)`.
     Note: cloning the *current* view means a variant of a variant copies the variant's layout, which
     is what a user expects when they refine "Compact" into "Compact 2".
  4. `variants = ((IModelViewVariants)root).Variants`; if empty add `Default` → root. Add
     `{Id=newId, Caption, View=clone}`; `variants.Current` = new.
  5. `Application.SaveModelChanges()`; D7 with `newId` so the frame shows the new variant and
     *ChangeVariant* lists it.
- `SimpleAction` **DeleteVariant**: active only when the current view's user-layer node is
  `IsNewNode` (D6). Removes the `Variants` entry, `((IModelNode)View.Model).Remove()`, sets `Current`
  to `Default`, saves, D7 with the root Id.
- Gate: manual: Acme > Save As Variant "Compact" > Customize Layout, move Street > *ChangeVariant*
  toggles between Default (Street col 1) and Compact (Street col 2). Automated in MERGE-008.

### MERGE-007: Merge To Module learns variants (3h)

`MergeToModuleController.cs` + `XafmlViewMerger.cs`.

- Drop the `IsNewNode` refusal. Branch after the merge (D4):
  - view element `IsNewNode="True"` in the user layer → `((IModelNode)View.Model).Remove()`;
  - else → `Undo()` as today.
  Do this **after** D7 has switched the frame to the root view; the live view must not hold the node
  being removed. Simplest: merge, `Frame.SetView(root view)`, then remove, then `SaveModelChanges()`,
  then `ClearEmptyUserAspects`.
- D3: if `root != View.Model` and the root's user-layer element has a `Variants` child, build the
  pruned root element and call `MergeViewIntoFile` a second time, then `((ModelNode)root.Variants).Undo()`.
  `XafmlViewMerger` needs no new operation: an `IsNewNode` source with absent target already creates
  `{key, IsNewNode}` and merges values and children (MERGE-003 row 4), and a `Variants` child under an
  existing root merges node by node.
- Message: "Merged Customer_DetailView_Compact and Customer_DetailView/Variants into … Rebuild and restart."
- Unit tests in `XafmlViewMergerTests`: (h) `IsNewNode` view with absent target → complete node with
  the marker, children intact; (i) pruned root element with `Variants` only → root keeps its other
  values, `Variants` and `Current` written; (j) second merge of the same variant after a rebuild
  (target now exists, source not `IsNewNode`) → values updated, marker kept.
- README limitations: replace "Views defined in code or a module only" with the D4 rule; add D1.

### MERGE-008: E2E gates (6h)

Two new tests in `MergeRoundTripTests` reusing `BuildAndStart`, `RestartAndLogin`, `Column2Labels`,
`LayoutGroup`, `UserLayerXml`; state reset must also strip `Customer_ListView`,
`Customer_DetailView/Variants` and any `Customer_DetailView_*` node from the module xafml.

1. **SplitListView_Merge_Restart**: open Customer list, Tools > Master-Detail > Detail Below, assert
   the split renders (a DetailView form under the grid: the DOM has both `.dxbl-grid` and the
   layout container in one frame; pin the selector during implementation), *Merge To Module*, assert
   `Customer_ListView` in the xafml has `MasterDetailMode="ListViewAndDetailView"` and
   `SplitLayout Direction="Vertical"`, user layer clean; restart, assert the split renders and the
   user layer is still clean.
2. **SaveAsVariant_Merge_Restart_ChangeVariantSwitches**: open Acme, Save As Variant "Compact",
   move Street to column 2, *Merge To Module*, assert `Customer_DetailView_Compact IsNewNode="True"`
   with Street in `Customer_col2`, `Customer_DetailView/Variants` has `Default` and `Compact`,
   user layer contains neither Id; restart, open Acme, *ChangeVariant* > Compact → Street in col 2,
   *ChangeVariant* > Default → default layout; user layer clean after the switches except `Current`.
3. Manual Model Editor check on the merged file, as for MERGE-003, hash unchanged. The variant node
   is a full copy, so the file grows by one view; that is what the ME would have produced too.

### Docs (1h, part of MERGE-008)

README "Try it" gets the two new flows; HOW-TO-IMPLEMENT lists the two new controllers and the
ViewVariants registration; DESIGN.md gets the D1 to D7 table. Layer diagram unchanged.

## Estimate

| Card | Hours |
|---|---|
| MERGE-004 | 1 |
| MERGE-005 | 3 |
| MERGE-006 | 4 |
| MERGE-007 | 3 |
| MERGE-008 | 7 |
| Total | 18 |

## Open risks (as of implementation)

- D7 settled from source: `ListView` reads `MasterDetailMode` in its constructor, so the view is
  re-created via `CreateShortcut` / `SetView(null)` / `ProcessShortcut`, the path
  `ResetViewSettingsController` uses.
- The Blazor split-view DOM selector is `.xaf-masterdetail-container.direction-vertical|horizontal`;
  undocumented, may need a bump on a DX upgrade.
- `IModelVariants.Current` is also where the runtime stores each user's last selection, so it lands in
  the module as whatever the admin last picked. Same as the ME's `Current`, documented, not fixed.
- Layout re-render after a view swap lags the toolbar by a moment; the E2E polls the column labels
  for up to 15 s instead of asserting immediately.
