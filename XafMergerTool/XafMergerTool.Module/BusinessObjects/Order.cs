using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace XafMergerTool.Module.BusinessObjects;

public enum OrderStatus { Draft, Confirmed, Shipped, Cancelled }

[DefaultClassOptions]
[DefaultProperty(nameof(Number))]
public class Order : BaseObject
{
    public virtual string Number { get; set; } = string.Empty;
    public virtual DateTime Date { get; set; } = DateTime.Today;
    public virtual decimal Amount { get; set; }
    public virtual OrderStatus Status { get; set; }

    [Browsable(false)]
    public virtual Guid? CustomerId { get; set; }
    [ForeignKey(nameof(CustomerId))]
    public virtual Customer Customer { get; set; }
}
