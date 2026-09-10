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
        merge.Active[DevOnlyKey] = Debugger.IsAttached || config.GetValue<bool>("XafModelMerge:Enabled");
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
            Application.ShowViewStrategy.ShowMessage($"No user-layer changes for {viewId}.", InformationType.Info);
            return;
        }
        // A view the user layer created (Save As Variant) is merged whole and removed; its other aspects hold only
        // the localizable defaults XAF writes when it shows a new DetailView (CaptionColon, RequiredFieldMark) and
        // are dropped. A module-defined view keeps the refusal: Undo() clears every aspect, only aspect 0 is merged.
        var userCreated = (string?)view.Attribute("IsNewNode") == "True";
        if (!userCreated)
            for (var i = 1; i < userLayer.AspectCount; i++)
                if (UserLayer.ViewDiff(userLayer, viewId, i) != null)
                    throw new UserFriendlyException($"{viewId} has localized changes (aspect '{userLayer.GetAspect(i)}'); only the default aspect is merged.");

        // A variant is reachable only through its root view's Variants node, which lives in the user layer too:
        // merge that subtree along, and nothing else of the root (D3).
        var rootId = Frame.GetController<ChangeVariantController>()?.CurrentFrameViewVariantsManager?.Variants?.RootViewId ?? viewId;
        var rootDiff = rootId == viewId ? null : UserLayer.ViewDiff(userLayer, rootId);
        var rootVariants = rootDiff?.Element("Variants");
        if (rootId == viewId)
            foreach (var v in view.Element("Variants")?.Elements() ?? [])
                if (UserLayer.IsUserCreated(Application, (string?)v.Attribute("ViewID") ?? ""))
                    throw new UserFriendlyException($"Variant {v.Attribute("ViewID")} exists only in the user layer; open it and merge it first.");

        var path = ResolveModulePath();
        void MergeFiles()
        {
            XafmlViewMerger.MergeViewIntoFile(path, view);
            if (rootVariants != null) // the root's own element name: root and variant need not be the same view kind
                XafmlViewMerger.MergeViewIntoFile(path, new XElement(rootDiff!.Name, new XAttribute("Id", rootId), rootVariants));
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
            ((ModelNode)View.Model).Undo();
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
        Application.ShowViewStrategy.ShowMessage(
            $"Merged {merged} into {path}. Rebuild and restart to load it from the module.", InformationType.Success, 8000);
    }

    string ResolveModulePath()
    {
        var config = Application.ServiceProvider.GetRequiredService<IConfiguration>();
        var configured = config["XafModelMerge:ModuleXafmlPath"];
        if (string.IsNullOrWhiteSpace(configured))
            throw new UserFriendlyException("XafModelMerge:ModuleXafmlPath is not configured.");
        var env = Application.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        return Path.GetFullPath(configured, env.ContentRootPath);
    }
}
