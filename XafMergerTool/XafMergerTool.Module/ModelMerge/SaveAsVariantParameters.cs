using System.ComponentModel;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Validation;

namespace XafMergerTool.Module.ModelMerge;

/// <summary>Popup parameters for Save As Variant: the caption the ChangeVariant action will show.</summary>
[DomainComponent]
public class SaveAsVariantParameters
{
    [RuleRequiredField]
    public string Caption { get; set; } = "";
}
