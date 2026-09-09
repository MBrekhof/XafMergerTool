using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace XafMergerTool.Module.ModelMerge;

/// <summary>
/// Pure XML: takes one view element out of a serialised model layer and splices it into an xafml file.
/// Nothing here knows about XAF; the controller supplies the layer XML and the target path.
/// </summary>
public static class XafmlViewMerger
{
    /// <summary>The Views/* element with the given Id from a layer's XML, or null when the layer has no diff for that view.</summary>
    public static XElement? FindView(string layerXml, string viewId)
    {
        if (string.IsNullOrWhiteSpace(layerXml)) return null;
        return XDocument.Parse(layerXml).Root?
            .Element("Views")?.Elements()
            .FirstOrDefault(e => (string?)e.Attribute("Id") == viewId);
    }

    /// <summary>
    /// Replaces the same-Id view element in the file, or inserts it in Id order (the order ModelXmlWriter
    /// emits: Index first, then Id). Last write wins: any existing diff for the view is dropped.
    /// </summary>
    public static void MergeViewIntoFile(string xafmlPath, XElement view)
    {
        var doc = File.Exists(xafmlPath) ? XDocument.Load(xafmlPath) : new XDocument(new XElement("Application"));
        var root = doc.Root ?? throw new InvalidOperationException($"{xafmlPath} has no root element.");
        var views = root.Element("Views");
        if (views == null)
        {
            views = new XElement("Views");
            InsertSorted(root, views, e => e.Name.LocalName);
        }
        var viewId = (string?)view.Attribute("Id") ?? throw new ArgumentException("View element has no Id.", nameof(view));
        var existing = views.Elements().FirstOrDefault(e => (string?)e.Attribute("Id") == viewId);
        var merged = new XElement(view);
        // A view the module itself created carries IsNewNode; dropping it would make the layer an override of nothing.
        if ((string?)existing?.Attribute("IsNewNode") == "True" && merged.Attribute("IsNewNode") == null)
            merged.SetAttributeValue("IsNewNode", "True");
        existing?.Remove();
        InsertSorted(views, merged, e => (string?)e.Attribute("Id") ?? "");

        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", NewLineChars = "\r\n", Encoding = new UTF8Encoding(true) };
        using var writer = XmlWriter.Create(xafmlPath, settings);
        doc.Save(writer);
    }

    // Mirrors SortChildNodesHelper.DoSortNodesByDefault: indexed nodes first by Index, then by Id.
    static void InsertSorted(XElement parent, XElement child, Func<XElement, string> key)
    {
        var next = parent.Elements().FirstOrDefault(e => Compare(e, child, key) > 0);
        if (next != null) next.AddBeforeSelf(child); else parent.Add(child);
    }

    static int Compare(XElement a, XElement b, Func<XElement, string> key)
    {
        int? ia = (int?)a.Attribute("Index"), ib = (int?)b.Attribute("Index");
        if (ia.HasValue != ib.HasValue) return ia.HasValue ? -1 : 1;
        var byIndex = ia.HasValue ? ia.Value.CompareTo(ib!.Value) : 0;
        return byIndex != 0 ? byIndex : Comparer<string>.Default.Compare(key(a), key(b));
    }
}
