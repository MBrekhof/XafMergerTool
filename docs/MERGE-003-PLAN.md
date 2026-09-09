# MERGE-003 plan: node-by-node merge that preserves module-created nodes

Status: revision 3, 2026-09-09. Codex reviewed revisions 1 to 3 against the DX 26.1 source; the algorithm
and scope were confirmed on revision 3, wording fixed after. Implemented in XafmlViewMerger.cs.

## Problem

`XafmlViewMerger.MergeViewIntoFile` replaces the module's view element wholesale with the user
diff. A layout node the module created in xafml (`IsNewNode="True"`, e.g. a hand-added tab or group)
that the user diff overrides without repeating the marker becomes an override of nothing after the
merge and XAF drops it (`ModelNode.CreateMasterNode`, ModelNode.cs 2080-2100: a layer node that is
not `IsNewNode` with no root below is unusable). Values the module set on nodes the diff does not
mention vanish too, which is more than "last write wins for the same node".

## Reference semantics

XAF's own layer move, `ModelNode.MoveNode` (ModelNode.cs 3751-3807, DX 26.1), with source = the
node in the layer being merged away (user) and target = the same-key node in the destination layer
(module), or null. Branches are ordered; "otherwise" excludes earlier rows.

| Source state | Target state | Action | Then |
|---|---|---|---|
| Removed, not New | exists, New | delete target node | stop (no value/child merge) |
| Removed, not New | exists, not New | target.Removed = true | stop |
| Removed, not New | absent | create target `{key, Removed=true}` | stop |
| otherwise | absent | create target `{key, IsNewNode = source.IsNewNode, Removed = source.Removed}` (deviation 1b) | merge |
| otherwise | exists, different node type | delete target; create `{key, IsNewNode=true, Removed = target.Removed \|\| !target.New}` (deviation 1c) | merge |
| Removed and New | exists, same type | clear target values and children; Removed = target.Removed \|\| !target.New; IsNewNode = true | merge |
| otherwise | exists, Removed and not New | target.IsNewNode = source.IsNewNode | merge |
| otherwise | exists | no state change | merge |

"Merge values" = each source value overwrites the target's, target values not in the source stay
(MoveValues, 3815-3824). "Merge children" = recurse per source child matched by key; target children
not in the source stay (MoveNodes, 3828-3835). Layer composition reads `Removed` before `IsNewNode`
within one layer node (CreateMasterNode 2090-2096), so a node carrying both means "drop what is
below, define here".

## Deliberate deviations from MoveNode

1. **Suppression preservation** (review finding 1, both rounds). XAF's move can drop a `Removed`
   marker in three places. This tool clears the user layer after the merge, so the module layer must
   carry the suppression on its own:
   - 1a. Row 1: a New target that also carries `Removed` (a module replacement of a generated node)
     is reduced to a pure tombstone `{key, Removed=true}` instead of deleted.
   - 1b. Absent target: the created node copies the source's `Removed` as well as `IsNewNode`, so a
     user replacement of a generated node (both markers) stays a replacement.
   - 1c. Different type: the replacement node's `Removed` is `target.Removed || !target.New`, so an
     existing suppression survives the type change.
2. **Removed source with absent target** always creates the tombstone. XAF asks the live model
   (`DoesNodeExist`); the file has no such oracle. An override of a node that does not exist below is
   unusable and ignored by XAF, so the worst case is a dead line in the file.
3. **Policy = `RuntimeModelNodeMoveInfo`** (finding 3), owned in full: every value moves, every
   child moves, no existence check. The runtime app reads the module xafml with the metadata of all
   loaded modules, so what the Blazor app wrote, the Blazor app can read back; nothing beyond that is
   claimed. The Model Editor opened on the platform-agnostic
   module alone lacks platform-specific properties and node types (e.g. Blazor ListView extensions)
   and shows them as unusable. Documented; users who merge ListView diffs point `ModuleXafmlPath` at
   the Blazor project's `Model.xafml` instead. Whether that file's metadata covers every Blazor
   extension is not verified here.

## Design

Replace the body of `MergeViewIntoFile` with `MergeNode(XElement parent, XElement? target, XElement source, bool targetParentNew)`
mirroring the table on `XElement`s:

- **Key** (finding Q1): the `Id` attribute when present; otherwise the element name. Keyless
  property nodes (`Layout`, `Items`, `Columns`) are normal under `Views`.
- **State**: `IsNewNode` and `Removed` parsed with `bool.TryParse` (reader uses `bool.Parse`, so
  `true`/`True` both count). **Effective New** of a target node = its own attribute, or its parent's
  effective New unless the node is Removed (finding 4: the reader makes children of a New node New).
  The table is evaluated on effective state; attributes written back are explicit.
- **Type** (finding 6): different element name = different type, no alias handling. The only
  accepted input is canonical writer output (the user layer is written by `ModelXmlWriter`; the module
  file by the Model Editor, which uses the same writer). A hand-edited module file using the generic
  `Item` alias for a node the diff names canonically gets a type replacement it did not need: the
  target's unspecified values and children are discarded and a needless `Removed` marker is written.
  Documented.
- **Values**: every attribute other than key and state; `target.SetAttributeValue(name, value)`.
- **Children**: recurse. **Order** (finding 8, hit for real by the E2E gate during implementation):
  the writer sorts by the *composed* `Index`, which is not in the file, so sorting by local attributes
  put unindexed nodes last and broke the gate. Instead: existing target children keep their places; a
  new child is placed after the counterpart of its previous source sibling, else last. The source was
  written by `ModelXmlWriter`, so its relative order is the writer's. `Index=""` is an explicit null
  override (finding 7) and is copied as written.
- **Delete** = `target.Remove()`. Tombstone reduction per deviation 1.
- The view element itself goes through the same function with parent = `Views` and
  `targetParentNew = false`. This subsumes the current `IsNewNode` carry-over special case.
- No pruning of key-only nodes (finding 8): the writer's rule depends on model context. The promise
  is "XAF composes the same model", not "byte-identical to a writer save"; the manual Model Editor
  check remains the acceptance for byte-stability on the sample.

Not touched: controller, user-layer clearing, aspect handling.

## Known limitation kept (finding 2)

A change the user layer made on top of an intervening layer (the app project's `Model.xafml`, a
tenant layer) lands below that layer after the merge and can be overridden by it. Values: module
`Index=2` loses to app `Index=1`. Structure too: a node the app layer created and the user removed
is re-established by the app's `IsNewNode` once the removal sits below it. Existing architecture, not
introduced here. The promise is therefore "XAF composes the same model **when no layer between the
target and the user layer touches the view**". README says: merge into the highest file layer you own
(`Blazor.Server/Model.xafml`) when the app layer customises the same view.

## Consequences

- "Last write wins" becomes node-level: for a node the diff mentions, its values and state win;
  everything else in the module's view stays.
- A module-created group the user emptied at runtime: the editor writes `Removed="True"` on it in
  the user layer → row 1 deletes the module's creation (or tombstones it if it replaced a generated
  node). Correct.
- A module-created group the user only rearranged inside: its element in the diff has no state →
  last row: the module's `IsNewNode` survives. This is the MERGE-003 case.

## Tests

`XafmlViewMergerTests`, module xafml with
`<LayoutGroup Id="Extra" IsNewNode="True" Caption="Extra"><LayoutItem Id="Notes" /></LayoutGroup>`
(Notes is effectively New by inheritance) inside `Customer_DetailView`, plus a generated-node
replacement `<LayoutGroup Id="Gen" Removed="True" IsNewNode="True" />`. User diffs:
(a) move an item into `Extra` → `Extra` keeps `IsNewNode`, `Caption` stays;
(b) remove `Notes` → Notes deleted (inherited New), not tombstoned;
(c) remove `Gen` → `Gen` becomes `Removed="True"` only;
(d) re-create `Extra` as `TabbedGroup` → old deleted, new `TabbedGroup Id="Extra" IsNewNode="True"`;
(e) `Index=""` on a sibling does not throw and is kept; new children follow source order;
(f) user replaces generated `G` (both markers) with no module entry → module gets both markers (1b);
(g) module `Gen` (both markers) re-created by the user as `TabbedGroup` → new node keeps `Removed` (1c).
These assert markers in the file. Composition itself is exercised by the E2E gate: a second test
merges Street to column 2, restarts, moves it back and merges again on top of the module's diff, then
restarts and asserts the default layout. The twice-merged file (col2 Street deleted, col1 Street
`Removed`+`IsNewNode`) opened in the standalone Model Editor 26.1 with Save disabled, closed without a
prompt, hash unchanged (2026-09-09). A composition harness for synthetic layers is out of scope.
