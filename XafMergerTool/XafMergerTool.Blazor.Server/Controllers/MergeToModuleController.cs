using System.Diagnostics;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using DevExpress.ExpressApp.Model;
using DevExpress.ExpressApp.Model.Core;
using DevExpress.ExpressApp.ViewVariantsModule;
using System.Xml.Linq;
using DevExpress.Persistent.Base;
using XafMergerTool.Module.ModelMerge;

namespace XafMergerTool.Blazor.Server.Controllers;

/// <summary>
/// "Merge To Module": writes the current view's user-layer diff into the module's Model.DesignedDiffs.xafml
/// and clears it from the user layer. Developer-machine only: needs the source checked out.
/// </summary>
public class MergeToModuleController : ViewController
{
    const string DevOnlyKey = "DevOnly";
    const string EnabledKey = "XafModelMerge:Enabled";
    const string ModulePathKey = "XafModelMerge:ModuleXafmlPath";
    /// <summary>
    /// Used when the config key is absent. Some apps keep appsettings out of git (connection strings), so the
    /// default lives in code and the key is an override, e.g. "Model.xafml" to merge into the platform layer.
    /// </summary>
    const string DefaultModulePath = "../XafMergerTool.Module/Model.DesignedDiffs.xafml";
    readonly SimpleAction merge;

    public MergeToModuleController()
    {
        TargetViewType = ViewType.Any;
        merge = new SimpleAction(this, "MergeToModule", PredefinedCategory.Tools)
        {
            Caption = "Merge To Module",
            ImageName = "ModelEditor_ModelMerge",
            SelectionDependencyType = SelectionDependencyType.Independent,
            ToolTip = "Write this view's runtime customisations into the module xafml and remove them from the user layer."
        };
        merge.Execute += Merge_Execute;
    }

    protected override void OnActivated()
    {
        base.OnActivated();
        var config = Application.ServiceProvider.GetRequiredService<IConfiguration>();
        merge.Active[DevOnlyKey] = Debugger.IsAttached || config.GetValue<bool>(EnabledKey);
    }

    protected override void OnDeactivated()
    {
        merge.Active.RemoveItem(DevOnlyKey);
        base.OnDeactivated();
    }

    void Merge_Execute(object sender, SimpleActionExecuteEventArgs e)
    {
        var userLayer = UserLayer.Get(Application)
            ?? throw new UserFriendlyException("No user model layer is loaded; is the Security System enabled?");

        var viewId = View.Model.Id;
        var view = UserLayer.ViewDiff(userLayer, viewId);
        if (view == null)
        {
            Application.ShowViewStrategy.ShowMessage($"No user-layer changes for {viewId}. (Close the Customize Layout form first; closing it is what saves the layer.)", InformationType.Info);
            return;
        }
        var userCreated = (string?)view.Attribute("IsNewNode") == "True";
        var path = TargetFor(ResolveModulePath(), viewId, out var targetNote);

        // Language aspects (D5). Undo() clears every aspect of the view, so whatever the other aspects hold must be
        // merged as well or it is lost. A module-defined view's aspect diffs go to the target's localization sibling
        // (Model.DesignedDiffs.Localization.nl-NL.xafml): a caption renamed in the layout designer lands in the
        // current UI language's aspect, so any app that runs in a non-English culture has these on most views.
        // A view the user layer created (Save As Variant) is merged whole and removed; its other aspects hold only
        // the localizable defaults XAF writes when it shows a new DetailView (CaptionColon, RequiredFieldMark) and
        // are dropped.
        var localized = new List<(string Aspect, XElement Diff, string File)>();
        if (!userCreated)
            for (var i = 1; i < userLayer.AspectCount; i++)
            {
                var aspect = userLayer.GetAspect(i);
                var aspectDiff = UserLayer.ViewDiff(userLayer, viewId, i);
                if (aspectDiff == null) continue;
                var file = XafmlViewMerger.FindLocalizationFile(path, aspect)
                    ?? throw new UserFriendlyException(
                        $"{viewId} has changes in the '{aspect}' aspect, but there is no localization xafml for that language next to {path}. " +
                        "Add one (as EmbeddedResource) or reset that aspect for this view; nothing was merged.");
                localized.Add((aspect, aspectDiff, file));
            }

        // A user-created variant is reachable only through its root view's Variants node, which lives in the user
        // layer too: merge that subtree along, and nothing else of the root (D3). For a variant that already exists
        // in source, the root's Variants diff is only the user's Current pick, which stays in the user layer.
        var variantsManager = Frame.GetController<ChangeVariantController>()?.CurrentFrameViewVariantsManager;
        var rootId = variantsManager?.Variants?.RootViewId ?? viewId;
        var rootDiff = rootId == viewId || !userCreated ? null : UserLayer.ViewDiff(userLayer, rootId);
        var rootVariants = rootDiff?.Element("Variants") is { } rv ? new XElement(rv) : null;
        // Current is a per-user pick, never a module default: it is dropped from whatever is merged (CARD-2073).
        foreach (var variants in new[] { view.Element("Variants"), rootVariants })
        {
            variants?.Attribute("Current")?.Remove();
            if (variants is { HasElements: false, HasAttributes: false, Parent: not null }) variants.Remove();
        }
        if (rootVariants is { HasElements: false, HasAttributes: false }) rootVariants = null;
        // The root goes to its own target: it may be a view the application project's Model.xafml creates.
        var rootPath = rootVariants == null ? path : TargetFor(ResolveModulePath(), rootId, out _);
        if (!view.HasElements && view.Attributes().All(a => a.Name == "Id"))
        {
            Application.ShowViewStrategy.ShowMessage($"No user-layer changes for {viewId} besides the variant choice.", InformationType.Info);
            return;
        }
        if (rootId == viewId)
            foreach (var v in view.Element("Variants")?.Elements() ?? [])
                if (UserLayer.IsUserCreated(Application, (string?)v.Attribute("ViewID") ?? ""))
                    throw new UserFriendlyException($"Variant {(string?)v.Attribute("ViewID")} exists only in the user layer; open it and merge it first.");

        RefuseIfPlatformLayerOverrides(path, view, aspect: null);
        if (rootVariants != null)
            RefuseIfPlatformLayerOverrides(rootPath, new XElement(rootDiff!.Name, new XAttribute("Id", rootId), rootVariants), aspect: null);
        foreach (var l in localized)
            RefuseIfPlatformLayerOverrides(path, l.Diff, l.Aspect);

        var written = new List<string> { path };
        void MergeFiles()
        {
            XafmlViewMerger.MergeViewIntoFile(path, view);
            if (rootVariants != null) // the root's own element name: root and variant need not be the same view kind
            {
                XafmlViewMerger.MergeViewIntoFile(rootPath, new XElement(rootDiff!.Name, new XAttribute("Id", rootId), rootVariants));
                if (!written.Contains(rootPath)) written.Add(rootPath);
            }
            foreach (var l in localized)
            {
                XafmlViewMerger.MergeViewIntoFile(l.File, l.Diff);
                if (!written.Contains(l.File)) written.Add(l.File);
            }
        }
        void ClearUserLayer()
        {
            if (rootVariants != null)
                ((ModelNode)((IModelViewVariants)Application.Model.Views[rootId]).Variants).Undo();
            Application.SaveModelChanges();
            UserLayer.ClearEmptyAspects(Application);
        }

        if (!userCreated)
        {
            MergeFiles();
            // Same call ResetViewSettingsController makes: drops the last (user) layer's subtree for this view.
            // On a root view that subtree holds the user's variant pick too; put it back (CARD-2073).
            var pick = (View.Model as IModelViewVariants)?.Variants.Current;
            ((ModelNode)View.Model).Undo();
            if (pick != null) ((IModelViewVariants)View.Model).Variants.Current = pick;
            ClearUserLayer();
        }
        else
        {
            // The frame's view sits on the node being removed: detach before anything is written, remove, re-attach
            // on the root (D4); a failure puts the root back before it surfaces, as in ListViewSettingsController.
            var shortcut = View.CreateShortcut();
            shortcut.ViewId = rootId;
            if (!Frame.SetView(null))
                throw new UserFriendlyException("The view could not be closed; nothing was merged.");
            try
            {
                MergeFiles();
                ((IModelNode)Application.Model.Views[viewId]).Remove();
                ClearUserLayer();
                // The ViewVariants frame manager caches the root's variants with Current = this view; let it re-read
                // the model, which no longer has the view, before the root goes back into the frame.
                variantsManager?.RefreshVariants();
                Frame.SetView(Application.ProcessShortcut(shortcut));
            }
            catch
            {
                if (Frame.View == null)
                    try { Frame.SetView(Application.ProcessShortcut(shortcut)); } catch { /* original exception wins */ }
                throw;
            }
        }

        var merged = rootVariants == null ? viewId : $"{viewId} and {rootId}/Variants";
        if (localized.Count > 0) merged += $" (aspects: {string.Join(", ", localized.Select(l => l.Aspect))})";
        Application.ShowViewStrategy.ShowMessage(
            $"Merged {merged} into {string.Join(" and ", written)}.{targetNote} Rebuild and restart to load it from source.", InformationType.Success, 8000);
    }

    string ResolveModulePath()
    {
        var config = Application.ServiceProvider.GetRequiredService<IConfiguration>();
        var configured = config[ModulePathKey];
        var path = Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? DefaultModulePath : configured, ContentRoot());
        if (!File.Exists(path))
            throw new UserFriendlyException(
                $"{path} does not exist. Merge To Module needs the source tree checked out (run from Visual Studio or `dotnet run` in the project folder); set {ModulePathKey} to override the target.");
        return path;
    }

    /// <summary>
    /// A view that the application project's Model.xafml *creates* (IsNewNode there: a dashboard built around a
    /// Razor component, a chart list with a platform editor) exists only in that layer, so its customisations
    /// belong there too; merging them into the module would put the view's layout below the layer that defines
    /// it. Such a view is merged into the platform file instead of the configured target, and the message says
    /// so. Any other view keeps the configured target.
    /// </summary>
    string TargetFor(string configuredPath, string viewId, out string note)
    {
        note = "";
        var platformPath = Path.Combine(ContentRoot(), "Model.xafml");
        if (!File.Exists(platformPath) ||
            string.Equals(Path.GetFullPath(platformPath), Path.GetFullPath(configuredPath), StringComparison.OrdinalIgnoreCase))
            return configuredPath;
        var upper = XafmlViewMerger.FindView(File.ReadAllText(platformPath), viewId);
        if ((string?)upper?.Attribute("IsNewNode") != "True") return configuredPath;
        note = $" {viewId} is defined by the application project's Model.xafml, so it was merged there instead of into the module.";
        return platformPath;
    }

    /// <summary>
    /// The application project's Model.xafml (and its Model_xx-XX.xafml for a language aspect) sits above the
    /// module. A diff merged into the module lands below it and is overridden where both touch the same node:
    /// same attribute, or a Removed/IsNewNode marker on a shared node (<see cref="XafmlViewMerger.Overlaps"/>).
    /// Refuse only then; a platform node that merely carries an EditorTypeName while the diff moves columns is
    /// fine. Skipped when the target already is the platform file.
    /// </summary>
    void RefuseIfPlatformLayerOverrides(string targetPath, XElement diff, string? aspect)
    {
        var platformPath = Path.Combine(ContentRoot(), "Model.xafml");
        if (string.Equals(Path.GetFullPath(platformPath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase)) return;
        if (aspect != null) platformPath = XafmlViewMerger.FindLocalizationFile(platformPath, aspect) ?? "";
        if (platformPath == "" || !File.Exists(platformPath)) return;
        var viewId = (string?)diff.Attribute("Id") ?? "";
        var upper = XafmlViewMerger.FindView(File.ReadAllText(platformPath), viewId);
        if (upper == null || !XafmlViewMerger.Overlaps(upper, diff)) return;
        throw new UserFriendlyException(
            $"{viewId} is also customised in {platformPath}, which sits above the module layer and would override part of this " +
            $"merge. Either move that node into the module first, or set {ModulePathKey} to \"Model.xafml\" for this session.");
    }

    string ContentRoot() => Application.ServiceProvider.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
}
