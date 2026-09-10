using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using DevExpress.ExpressApp.Blazor.Editors;
using DevExpress.ExpressApp.Layout;
using DevExpress.ExpressApp.Model;
using DevExpress.Persistent.Base;
using XafMergerTool.Module.ModelMerge;

namespace XafMergerTool.Blazor.Server.Controllers;

/// <summary>
/// The Model Editor's ListView options at runtime: MasterDetailMode + SplitLayout Direction/ViewsOrder as one
/// "Master-Detail" placement choice, and MasterDetailView as a "Detail View" choice. Writes to the user layer
/// like Customize Layout does, so Merge To Module carries it to the module xafml unchanged.
/// </summary>
public class ListViewSettingsController : ViewController<ListView>
{
    const string GuardKey = "CanEditModel";
    const string ModeKey = "MasterDetailMode";
    const string EditorKey = "DxGridListEditor";

    sealed record Placement(string Caption, MasterDetailMode Mode, FlowDirection Direction, ViewsOrder Order);
    static readonly Placement[] Placements =
    {
        new("No Master-Detail", MasterDetailMode.ListViewOnly, FlowDirection.Horizontal, ViewsOrder.ListViewDetailView),
        new("Detail Right", MasterDetailMode.ListViewAndDetailView, FlowDirection.Horizontal, ViewsOrder.ListViewDetailView),
        new("Detail Below", MasterDetailMode.ListViewAndDetailView, FlowDirection.Vertical, ViewsOrder.ListViewDetailView),
        new("Detail Left", MasterDetailMode.ListViewAndDetailView, FlowDirection.Horizontal, ViewsOrder.DetailViewListView),
        new("Detail Above", MasterDetailMode.ListViewAndDetailView, FlowDirection.Vertical, ViewsOrder.DetailViewListView),
    };

    readonly SingleChoiceAction masterDetail;
    readonly SingleChoiceAction detailView;

    public ListViewSettingsController()
    {
        TargetViewNesting = Nesting.Root;
        masterDetail = new SingleChoiceAction(this, "MasterDetail", PredefinedCategory.Tools)
        {
            Caption = "Master-Detail",
            ImageName = "ModelEditor_Views",
            ItemType = SingleChoiceActionItemType.ItemIsMode,
            SelectionDependencyType = SelectionDependencyType.Independent,
            ToolTip = "Show the selected object's Detail View beside or under this list (MasterDetailMode + SplitLayout)."
        };
        foreach (var p in Placements) masterDetail.Items.Add(new ChoiceActionItem(p.Caption, p));
        masterDetail.Execute += MasterDetail_Execute;

        detailView = new SingleChoiceAction(this, "MasterDetailView", PredefinedCategory.Tools)
        {
            Caption = "Detail View",
            ImageName = "ModelEditor_DetailView",
            ItemType = SingleChoiceActionItemType.ItemIsMode,
            SelectionDependencyType = SelectionDependencyType.Independent,
            ToolTip = "Which Detail View the split layout shows (MasterDetailView)."
        };
        detailView.Execute += DetailView_Execute;
    }

    IModelListView Model => (IModelListView)View.Model;

    protected override void OnActivated()
    {
        base.OnActivated();
        var allowed = ModelEditingGuard.CanEditModel(Application);
        masterDetail.Active[GuardKey] = allowed;
        detailView.Active[GuardKey] = allowed;
        // Blazor supports the split layout only with DxGridListEditor (docs 113249); and a lookup popup is a root
        // ListView in its own frame but shares its node with the main list, so keep the action out of it.
        var supported = View.Editor is DxGridListEditor && // not the base: DxTreeListEditor shares it and has no split layout
            Frame.Context != TemplateContext.LookupWindow && Frame.Context != TemplateContext.LookupControl;
        masterDetail.Active[EditorKey] = supported;
        detailView.Active[EditorKey] = supported;
        if (!allowed || !supported) return;

        var model = Model;
        var current = Placements.FirstOrDefault(p => p.Mode == model.MasterDetailMode &&
            (p.Mode == MasterDetailMode.ListViewOnly || (p.Direction == model.SplitLayout.Direction && p.Order == model.SplitLayout.ViewsOrder)));
        masterDetail.SelectedItem = masterDetail.Items.First(i => i.Data == (current ?? Placements[0]));

        detailView.Items.Clear();
        foreach (var dv in Application.Model.Views.OfType<IModelDetailView>().Where(v => v.ModelClass == model.ModelClass).OrderBy(v => v.Id))
            detailView.Items.Add(new ChoiceActionItem(dv.Id, dv));
        // Same fallback chain the runtime uses: MasterDetailView, else DetailView, else the class default.
        var effective = model.MasterDetailView ?? model.DetailView ?? model.ModelClass.DefaultDetailView;
        detailView.SelectedItem = detailView.Items.FirstOrDefault(i => ((IModelDetailView)i.Data).Id == effective?.Id);
        detailView.Active[ModeKey] = model.MasterDetailMode == MasterDetailMode.ListViewAndDetailView;
    }

    protected override void OnDeactivated()
    {
        masterDetail.Active.RemoveItem(GuardKey);
        masterDetail.Active.RemoveItem(EditorKey);
        detailView.Active.RemoveItem(GuardKey);
        detailView.Active.RemoveItem(EditorKey);
        detailView.Active.RemoveItem(ModeKey);
        base.OnDeactivated();
    }

    void MasterDetail_Execute(object sender, SingleChoiceActionExecuteEventArgs e)
    {
        var p = (Placement)e.SelectedChoiceActionItem.Data;
        Recreate(model =>
        {
            model.MasterDetailMode = p.Mode;
            if (p.Mode == MasterDetailMode.ListViewOnly) return;
            model.SplitLayout.Direction = p.Direction;
            model.SplitLayout.ViewsOrder = p.Order;
        });
    }

    void DetailView_Execute(object sender, SingleChoiceActionExecuteEventArgs e)
    {
        var dv = (IModelDetailView)e.SelectedChoiceActionItem.Data;
        Recreate(model => model.MasterDetailView = dv);
    }

    // ListView reads MasterDetailMode in its constructor, so LoadModel is not enough: re-create the view in the
    // frame the way ResetViewSettingsController does (DX 26.1, ResetViewSettingsController.cs 134-143).
    void Recreate(Action<IModelListView> change)
    {
        var frame = Frame;
        var shortcut = View.CreateShortcut();
        var model = Model;
        // LightDictionary: its non-generic enumerator throws, so no LINQ here; the typed foreach is what DX uses.
        foreach (Controller c in frame.Controllers) ((ISupportUpdate)c).BeginUpdate();
        try
        {
            if (!frame.SetView(null)) return;
            change(model);
            Application.SaveModelChanges();
            frame.SetView(Application.ProcessShortcut(shortcut));
        }
        catch
        {
            // A Blazor frame without a view breaks the page: put the list back, then let the error surface.
            if (frame.View == null)
                try { frame.SetView(Application.ProcessShortcut(shortcut)); } catch { /* original exception wins */ }
            throw;
        }
        finally
        {
            foreach (Controller c in frame.Controllers) ((ISupportUpdate)c).EndUpdate();
        }
    }
}
