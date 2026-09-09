# How to add "Merge To Module" to your XAF Blazor app

Two files to copy, one config section, one rule about rebuilding. Written against DevExpress
XAF 26.1; the model-layer API it relies on (`ModelApplicationBase.LastLayer`, `ModelXmlWriter`,
`ModelNode.Undo`, `ModelDifferenceDbStore`) has been stable across the 2x.x line, so older versions
should work but are untested.

![The model layer stack before and after Merge To Module](merge-layers.png)

## 1. Check the prerequisites

- **XAF Blazor with runtime layout customisation.** Right-click a DetailView, you get *Customize
  Layout*. Available by default; `IModelOptions.CustomizationFormEnabled` must not be `false`.
- **User model differences stored in the database.** The Template Kit does this for you when you
  pick Standard security: `BlazorModule.cs` subscribes `CreateCustomUserModelDifferenceStore` and
  returns `new ModelDifferenceDbStore(app, typeof(ModelDifference), false, "Blazor")`. If your app
  does not, the runtime designer has nowhere to save and there is nothing to merge. Note the
  `"Blazor"` context id: the controller uses the same string.
- **EF Core or XPO.** The sample is EF Core (`DevExpress.Persistent.BaseImpl.EF.ModelDifference`).
  For XPO, change the `using` to `DevExpress.Persistent.BaseImpl` and the type is the same name.

## 2. Copy the XML splice into the module

`XafMergerTool.Module/ModelMerge/XafmlViewMerger.cs` → your platform-agnostic module, any folder.
Change the namespace. It has no XAF dependency, only `System.Xml.Linq`.

What it does: `FindView(layerXml, viewId)` picks `/Application/Views/*[@Id=viewId]` out of a
serialised model layer. `MergeViewIntoFile(path, view)` merges it into the target xafml node by
node, the way XAF's `ModelNode.MoveNode` moves a layer's node down: values and `IsNewNode`/`Removed`
state of nodes the diff mentions win, everything else in the module's view stays, and `Removed`
markers the module must keep carrying are preserved. It writes the file back with the declaration
and indentation the Model Editor uses. State table: `docs/MERGE-003-PLAN.md`.

## 3. Copy the controller into the Blazor.Server project

`XafMergerTool.Blazor.Server/Controllers/MergeToModuleController.cs` → your `*.Blazor.Server`
project. Change the namespace and the `using` for the merger.

Things you might want to change:

| Line | Default | Why you might change it |
|---|---|---|
| `PredefinedCategory.Tools` | Action lands on the Tools ribbon tab | Put it next to Save if you prefer |
| `TargetViewType = ViewType.Any` | DetailView and ListView | ListView merges column layout/widths too; drop to `DetailView` if you only want layouts |
| `"UserDiff"` check | Refuses when the last layer is not the user layer | Keep. It also refuses when security is off, which is correct: there is no user layer then |
| `"Blazor"` in `ClearEmptyUserAspects` | Must equal the context id in `BlazorModule.cs` | Only if you registered the store with another id |
| `config.GetValue<bool>("XafModelMerge:Enabled")` | Config gate besides `Debugger.IsAttached` | Rename, or drop the config gate and rely on the debugger only |

Do not remove the two refusals (localised aspects, user-created views) or the `Version` guard in
`ClearEmptyUserAspects`. They exist because `Undo()` clears more than gets merged, and because the
DB store refuses stale writes; the reasons are in `docs/DESIGN.md`.

## 4. Configure the target path

`appsettings.json`:

```json
"XafModelMerge": {
  "Enabled": false,
  "ModuleXafmlPath": "../YourApp.Module/Model.DesignedDiffs.xafml"
}
```

The path is resolved against the content root, which is the project folder under `dotnet run` and
Visual Studio, and the working directory when you start the exe by hand. `appsettings.Development.json`
gets `"Enabled": true` so the action shows without a debugger.

To merge into the Blazor-only layer instead, point it at `YourApp.Blazor.Server/Model.xafml`.

## 5. Use it

1. Run the app with a debugger attached, or with `Enabled` true.
2. Open a DetailView, right-click empty layout space, *Customize Layout*, rearrange, close the
   customisation form. Closing it is what saves the user layer; merge before that and you merge the
   previous state.
3. *Tools > Merge To Module*. The message names the file it wrote.
4. **Rebuild and restart.** The module xafml is an embedded resource, so the running app still has
   the old copy. The view shows the default layout until then, because the user layer was cleared.
5. Commit the xafml.

If the same view is merged again, the new diff is merged on top of the old one node by node. Last
write wins per node; there is no three-way merge.

## 6. Verify once

Open the module xafml in the Model Editor (double-click in Visual Studio, or the standalone
`DevExpress.ExpressApp.ModelEditor.v26.1.exe <module dll from the Blazor.Server bin> <module folder>`).
If it opens with Save disabled and closes without a prompt, the node ordering and markers are right.
If the ME rewrites the file on open, something about the splice does not match what the ME expects;
diff the two and check `docs/DESIGN.md` for the ordering rule.

## 7. What can go wrong

| Symptom | Cause | Fix |
|---|---|---|
| "No user-layer changes for X" | Customisation form still open, or nothing changed | Close the form first |
| "X has localized changes" | The user layer has a diff for the view in a language aspect | Out of scope; reset the view's localisation or merge it by hand |
| "X was created in the user layer" | The view exists only as a runtime-created node | Define the view in code or a module first |
| Layout reverts after restart | You did not rebuild, or the xafml is not `EmbeddedResource` | Rebuild; check the csproj |
| Layout applies twice / oddly | Stale user-layer row | Check `ModelDifferenceAspect` for the user; the action should have written `<Application/>` |
| ME "could not load DevExpress.Persistent.BaseImpl.EFCore" | Standalone ME given the module DLL from the module's own bin | Use the copy in the Blazor.Server bin |

## 8. What it deliberately does not do

Whole-model merge, anything outside `Views`, localisation, conflict resolution, WinForms, non-canonical
(hand-edited) xafml, and protecting a merged change from an intervening layer that customises the
same view. The limitations list in the README is the contract; extend from `docs/DESIGN.md` and
`docs/MERGE-003-PLAN.md` if you need more.
