using System.Text.RegularExpressions;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using DevExpress.ExpressApp.Model;
using DevExpress.ExpressApp.Model.Core;
using DevExpress.ExpressApp.ViewVariantsModule;
using DevExpress.Persistent.Base;
using XafMergerTool.Module.ModelMerge;

namespace XafMergerTool.Blazor.Server.Controllers;

/// <summary>
/// The Model Editor's "add a view, generate content, list it under Variants" at runtime: Save As Variant clones the
/// current view into the user layer under a new Id and registers it on the root view's Variants node; Delete Variant
/// removes a variant the user layer created. The ViewVariants module's own frame manager does the switching.
/// </summary>
public class SaveAsVariantController : ViewController<ObjectView>
{
    const string GuardKey = "CanEditModel";
    const string UserCreatedKey = "UserCreatedVariant";
    const string DefaultVariantId = "Default";

    readonly PopupWindowShowAction saveAs;
    readonly SimpleAction delete;

    public SaveAsVariantController()
    {
        TargetViewNesting = Nesting.Root;
        saveAs = new PopupWindowShowAction(this, "SaveAsVariant", PredefinedCategory.Tools)
        {
            Caption = "Save As Variant",
            ImageName = "ModelEditor_Clone",
            SelectionDependencyType = SelectionDependencyType.Independent,
            ToolTip = "Copy this view's current layout into a new view variant (Views > Variants in the Model Editor)."
        };
        saveAs.CustomizePopupWindowParams += SaveAs_CustomizePopupWindowParams;
        saveAs.Execute += SaveAs_Execute;

        delete = new SimpleAction(this, "DeleteVariant", PredefinedCategory.Tools)
        {
            Caption = "Delete Variant",
            ImageName = "ModelEditor_Delete",
            SelectionDependencyType = SelectionDependencyType.Independent,
            ConfirmationMessage = "Delete this variant? Only variants created at runtime can be deleted here.",
            ToolTip = "Remove a variant that was created at runtime from the user layer."
        };
        delete.Execute += Delete_Execute;
    }

    ICurrentFrameViewVariantsManager? Variants => Frame.GetController<ChangeVariantController>()?.CurrentFrameViewVariantsManager;
    IModelViews Views => Application.Model.Views;

    protected override void OnActivated()
    {
        base.OnActivated();
        var allowed = ModelEditingGuard.CanEditModel(Application);
        saveAs.Active[GuardKey] = allowed;
        delete.Active[GuardKey] = allowed;
        delete.Active[UserCreatedKey] = allowed && UserLayer.IsUserCreated(Application, View.Id);
    }

    protected override void OnDeactivated()
    {
        saveAs.Active.RemoveItem(GuardKey);
        delete.Active.RemoveItem(GuardKey);
        delete.Active.RemoveItem(UserCreatedKey);
        base.OnDeactivated();
    }

    void SaveAs_CustomizePopupWindowParams(object sender, CustomizePopupWindowParamsEventArgs e)
    {
        var os = Application.CreateObjectSpace(typeof(SaveAsVariantParameters));
        var p = os.CreateObject<SaveAsVariantParameters>();
        e.View = Application.CreateDetailView(os, p);
        e.DialogController.SaveOnAccept = false;
    }

    void SaveAs_Execute(object sender, PopupWindowShowActionExecuteEventArgs e)
    {
        var caption = ((SaveAsVariantParameters)e.PopupWindowViewCurrentObject).Caption.Trim();
        var root = RootView();
        var newId = $"{root.Id}_{Regex.Replace(caption, "[^A-Za-z0-9]", "")}";
        if (newId == root.Id || Views[newId] != null)
            throw new UserFriendlyException($"A view '{newId}' already exists; choose another caption.");

        // Complete copy of the composed view (values off their defaults, items, layout) into the user layer.
        var clone = (IModelView)((ModelNode)Views).AddClonedNode((ModelNode)View.Model, newId);
        var variants = ((IModelViewVariants)root).Variants;
        if (variants.Count == 0)
            AddVariant(variants, DefaultVariantId, DefaultVariantId, root);
        var added = AddVariant(variants, newId, caption, clone);
        variants.Current = added;
        Application.SaveModelChanges();

        ShowVariant(newId);
    }

    void Delete_Execute(object sender, SimpleActionExecuteEventArgs e)
    {
        var root = RootView();
        var variants = ((IModelViewVariants)root).Variants;
        var viewId = View.Id;
        var entry = variants.FirstOrDefault(v => v.View?.Id == viewId) ?? throw new UserFriendlyException($"{viewId} is not listed under {root.Id}/Variants.");
        var others = variants.Where(v => v != entry).ToList();
        var residue = others.All(v => v.View?.Id == root.Id); // only the Default that SaveAs added is left
        var fallback = others.FirstOrDefault(v => v.View?.Id == root.Id) ?? others.FirstOrDefault();

        // The frame's view sits on the node being removed: detach first, mutate, re-attach (same order as Recreate).
        var shortcut = View.CreateShortcut();
        shortcut.ViewId = root.Id;
        if (!Frame.SetView(null)) return;
        ((IModelNode)entry).Remove();
        ((IModelNode)Views[viewId]).Remove(); // exists only in the user layer, so this deletes it outright
        if (residue) ((ModelNode)variants).Undo(); else variants.Current = fallback;
        Application.SaveModelChanges();
        UserLayer.ClearEmptyAspects(Application);

        Frame.SetView(Application.ProcessShortcut(shortcut));
        if (!residue) ShowVariant(fallback!.View.Id);
    }

    IModelVariant AddVariant(IModelVariants variants, string id, string caption, IModelView view)
    {
        var v = variants.AddNode<IModelVariant>(id);
        v.View = view;
        // Caption is localizable and would go to the current culture's aspect (en-US); the Model Editor writes it
        // to the default aspect, and Merge To Module merges only that one.
        var model = (ModelApplicationBase)Application.Model;
        var aspect = model.CurrentAspect;
        model.SetCurrentAspect("");
        try { v.Caption = caption; }
        finally { model.SetCurrentAspect(aspect); }
        return v;
    }

    // The view whose Variants node lists the current view, per the ViewVariants module; the current view itself
    // when it is not a variant yet.
    IModelView RootView()
    {
        var rootId = Variants?.Variants?.RootViewId;
        return (rootId != null ? Views[rootId] : null) ?? View.Model;
    }

    void ShowVariant(string variantViewId)
    {
        var manager = Variants ?? throw new UserFriendlyException("The ViewVariants module is not active for this view.");
        manager.RefreshVariants();
        var info = manager.Variants?.Items.FirstOrDefault(i => i.ViewID == variantViewId)
            ?? throw new UserFriendlyException($"Variant for {variantViewId} not found after refresh.");
        manager.ChangeToVariant(info);
    }
}
