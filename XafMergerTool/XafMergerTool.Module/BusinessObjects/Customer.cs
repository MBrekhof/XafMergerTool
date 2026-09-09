using System.Collections.ObjectModel;
using System.ComponentModel;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace XafMergerTool.Module.BusinessObjects;

[DefaultClassOptions]
[DefaultProperty(nameof(Name))]
public class Customer : BaseObject
{
    public virtual string Name { get; set; } = string.Empty;
    public virtual string Email { get; set; } = string.Empty;
    public virtual string Phone { get; set; } = string.Empty;
    public virtual string Street { get; set; } = string.Empty;
    public virtual string PostalCode { get; set; } = string.Empty;
    public virtual string City { get; set; } = string.Empty;
    public virtual string Country { get; set; } = string.Empty;
    [FieldSize(FieldSizeAttribute.Unlimited)]
    public virtual string Notes { get; set; } = string.Empty;

    [Aggregated]
    public virtual IList<Order> Orders { get; set; } = new ObservableCollection<Order>();
}
