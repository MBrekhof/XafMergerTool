# XafMergerTool

**Question:** can a running XAF Blazor app write a view's user-layer customisations back into the
module's `Model.DesignedDiffs.xafml`, so they become part of the source?

**Answer: yes.** One action, *Merge To Module*, takes the current view's diff out of the user layer
(the `ModelDifference` table), splices it into the module xafml, and clears it from the user layer.
After rebuild and restart the view looks the same, the table no longer holds the view, and the module
xafml opens in the Model Editor untouched.

It is not runtime editing. It is the missing "save to source" button for the layout designer XAF
Blazor already has.

## What is in the box

- `XafMergerTool.Module`: `Customer` and `Order`, plus `ModelMerge/XafmlViewMerger.cs`, the XML
  splice (no XAF dependency).
- `XafMergerTool.Blazor.Server`: `Controllers/MergeToModuleController.cs`, the action. Shown on
  DetailView and ListView when `Debugger.IsAttached` or `XafModelMerge:Enabled` is true.
  `XafModelMerge:ModuleXafmlPath` points at the module xafml, relative to the content root
  (`appsettings.json`; Development turns `Enabled` on).
- `XafMergerTool.E2E`: the gate (C# Playwright, NUnit) and one unit check for the splice.
- `docs/DESIGN.md`: the mechanics, with the DevExpress source they come from.

Stack: DevExpress XAF 26.1, .NET 10, EF Core, SQL Server LocalDB, Standard security (Admin, no password).

## How the merge works

1. The user layer is `((ModelApplicationBase)Application.Model).LastLayer` (Id `UserDiff`). The
   action serialises it with XAF's own `ModelXmlWriter`, the same call `ModelDifferenceDbStore`
   uses to persist it, and takes `Views/*[@Id=<view>]`.
2. The element replaces any same-Id element in the module xafml, or is inserted in Id order, the
   order `ModelXmlWriter` emits. Markers (`IsNewNode`, `Removed`, `Index`) are kept as they are; the
   module layer needs them just as the user layer did.
3. `((ModelNode)View.Model).Undo()` drops the user layer's subtree for the view, the same call
   XAF's *Reset View Settings* makes, and `SaveModelChanges()` persists that.
4. `ModelDifferenceDbStore.SaveDifference` skips an aspect whose XML is empty, so a user layer that
   just lost its only diff would keep stale XML in the table. The action writes an empty
   `<Application/>` into such rows itself.

## The gate

`XafMergerTool.E2E/MergeRoundTripTests.cs`, one test:

1. Log in, open the Customer DetailView, assert the default two-column layout.
2. Right-click empty layout space, *Customize Layout*, drag *Street* from column 1 onto *Country*
   in column 2, close the customization form.
3. *Tools > Merge To Module*. Assert the module xafml has the layout node and the
   `ModelDifferenceAspect` table does not.
4. Kill the app, `dotnet build`, restart, log in again, assert *Street* is in column 2 via the DOM
   and the table still has no diff for the view.

The test hosts the app itself (builds it, starts the exe on port 5000, kills it), resets state first
(removes the view from the xafml, empties the ModelDifference tables), and restores the original
xafml afterwards. Needs LocalDB and the source tree.

```
cd C:\projects\XafMergerTool
dotnet test XafMergerTool\XafMergerTool.E2E
dotnet test XafMergerTool\XafMergerTool.E2E --settings XafMergerTool\XafMergerTool.E2E\headed.runsettings
```

Playwright's Chromium must be installed once after the first build:
`powershell -File XafMergerTool\XafMergerTool.E2E\bin\Debug\net10.0\playwright.ps1 install chromium`.

**Model Editor check (manual, done 2026-09-09):** the merged `Model.DesignedDiffs.xafml` opened in the
standalone Model Editor 26.1 with Save disabled, closed without a save prompt, and the file hash was
identical before and after. Open it with:

```
"C:\Program Files\DevExpress 26.1\Components\Tools\eXpressAppFrameworkNetCore\Model Editor\DevExpress.ExpressApp.ModelEditor.v26.1.exe" ^
  C:\projects\XafMergerTool\XafMergerTool\XafMergerTool.Blazor.Server\bin\Debug\net10.0\XafMergerTool.Module.dll ^
  C:\projects\XafMergerTool\XafMergerTool\XafMergerTool.Module
```

Pass the module DLL from the *Blazor.Server* bin: the module's own bin has no dependency DLLs and the
ME fails with "Could not load file or assembly DevExpress.Persistent.BaseImpl.EFCore".

## Limitations, on purpose

- **Developer machine only.** The action writes to a source file at a configured path; it needs the
  source checked out and a rebuild afterwards because the module xafml is an embedded resource.
- **Per view only.** Whole-model merge is where the layer semantics get ugly; not attempted.
- **Views only.** Navigation, actions, localisation and anything else outside `Views` are out of scope.
- **No conflict handling.** If the module already has a diff for the view, the user layer wins.
- **Default aspect only.** Only aspect 0 (unlocalised) is merged. A localised aspect for the view
  stays in the user layer.
- **One platform layer.** The diff lands in the platform-agnostic module. If it should be
  Blazor-only, point `ModuleXafmlPath` at `XafMergerTool.Blazor.Server/Model.xafml`.
- **Blazor only.** WinForms has the Model Editor and Model Merge Tool for this already.
