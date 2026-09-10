using System.Xml.Linq;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Model;
using DevExpress.ExpressApp.Model.Core;
using DevExpress.Persistent.BaseImpl.EF;

namespace XafMergerTool.Module.ModelMerge;

/// <summary>The per-user model layer (Id "UserDiff"), serialised the way ModelDifferenceDbStore persists it.</summary>
public static class UserLayer
{
    public static ModelApplicationBase? Get(XafApplication application)
    {
        var layer = ((ModelApplicationBase)application.Model).LastLayer;
        return layer?.Id == "UserDiff" ? layer : null;
    }

    public static XElement? ViewDiff(ModelApplicationBase layer, string viewId, int aspect = 0) =>
        XafmlViewMerger.FindView(new ModelXmlWriter().WriteToString(layer, aspect), viewId);

    /// <summary>True when the view exists only because the user layer created it (a runtime variant).</summary>
    public static bool IsUserCreated(XafApplication application, string viewId)
    {
        var layer = Get(application);
        var view = layer == null ? null : ViewDiff(layer, viewId);
        return (string?)view?.Attribute("IsNewNode") == "True";
    }

    // ModelDifferenceDbStore.SaveDifference skips aspects whose XML serialises to empty, so a user layer that just
    // lost its only diff (or a whole view) keeps its old XML in the table and re-applies it on restart. Clear it.
    public static void ClearEmptyAspects(XafApplication application)
    {
        var layer = Get(application);
        if (layer == null) return;
        var userId = ModelDifferenceDbStore.UserIdTypeConverter.ConvertToInvariantString(application.Security.UserId);
        using var os = application.CreateObjectSpace(typeof(ModelDifference));
        var diff = ModelDifferenceDbStore.FindModelDifference(os, typeof(ModelDifference), userId, "Blazor");
        // Same guard as SaveDifference: never overwrite a row newer than the layer we hold.
        if (diff == null || diff.Version > layer.Version) return;
        var writer = new ModelXmlWriter();
        for (var i = 0; i < layer.AspectCount; i++)
        {
            if (!string.IsNullOrEmpty(writer.WriteToString(layer, i))) continue;
            var aspect = ModelDifferenceDbStore.FindModelDifferenceAspect(diff, layer.GetAspect(i));
            if (aspect != null) aspect.Xml = ModelDifferenceDbStore.EmptyXafml;
        }
        os.CommitChanges();
    }
}
