using System.Diagnostics;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using DevExpress.ExpressApp.Model;
using DevExpress.ExpressApp.Model.Core;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;
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
        var userLayer = ((ModelApplicationBase)Application.Model).LastLayer;
        if (userLayer?.Id != "UserDiff")
            throw new UserFriendlyException("No user model layer is loaded; is the Security System enabled?");

        var viewId = View.Model.Id;
        var writer = new ModelXmlWriter();
        var view = XafmlViewMerger.FindView(writer.WriteToString(userLayer, 0), viewId);
        if (view == null)
        {
            Application.ShowViewStrategy.ShowMessage($"No user-layer changes for {viewId}.", InformationType.Info);
            return;
        }
        // Out of scope, so refuse rather than lose data: Undo() below clears every aspect of the view, but only
        // aspect 0 is merged; and a view the user created at runtime would leave its IsNewNode stub behind.
        for (var i = 1; i < userLayer.AspectCount; i++)
            if (XafmlViewMerger.FindView(writer.WriteToString(userLayer, i), viewId) != null)
                throw new UserFriendlyException($"{viewId} has localized changes (aspect '{userLayer.GetAspect(i)}'); only the default aspect is merged.");
        if ((string?)view.Attribute("IsNewNode") == "True")
            throw new UserFriendlyException($"{viewId} was created in the user layer; only views defined in code or a module can be merged.");

        var path = ResolveModulePath();
        XafmlViewMerger.MergeViewIntoFile(path, view);

        // Same call ResetViewSettingsController makes: drops the last (user) layer's subtree for this view.
        ((ModelNode)View.Model).Undo();
        Application.SaveModelChanges();
        ClearEmptyUserAspects(userLayer);

        Application.ShowViewStrategy.ShowMessage(
            $"Merged {viewId} into {path}. Rebuild and restart to load it from the module.", InformationType.Success, 8000);
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

    // ModelDifferenceDbStore.SaveDifference skips aspects whose XML serialises to empty, so a user layer
    // that just lost its only diff keeps its old XML in the table and re-applies it on restart. Clear it here.
    void ClearEmptyUserAspects(ModelApplicationBase userLayer)
    {
        var userId = ModelDifferenceDbStore.UserIdTypeConverter.ConvertToInvariantString(Application.Security.UserId);
        using var os = Application.CreateObjectSpace(typeof(ModelDifference));
        var diff = ModelDifferenceDbStore.FindModelDifference(os, typeof(ModelDifference), userId, "Blazor");
        // Same guard as SaveDifference: never overwrite a row newer than the layer we hold.
        if (diff == null || diff.Version > userLayer.Version) return;
        var writer = new ModelXmlWriter();
        for (var i = 0; i < userLayer.AspectCount; i++)
        {
            if (!string.IsNullOrEmpty(writer.WriteToString(userLayer, i))) continue;
            var aspect = ModelDifferenceDbStore.FindModelDifferenceAspect(diff, userLayer.GetAspect(i));
            if (aspect != null) aspect.Xml = ModelDifferenceDbStore.EmptyXafml;
        }
        os.CommitChanges();
    }
}
