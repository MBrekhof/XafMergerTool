using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace XafMergerTool.Module.ModelMerge;

/// <summary>
/// Pure XML: takes one view element out of a serialised model layer and merges it into an xafml file
/// node by node, the way XAF's own ModelNode.MoveNode moves a layer's node into a lower layer
/// (docs/MERGE-003-PLAN.md has the state table and the deliberate deviations).
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
    /// Merges the view into the file: for every node the diff mentions, its values and state win; nodes and
    /// values the diff does not mention stay. Markers the module needs to keep suppressing lower layers survive.
    /// </summary>
    public static void MergeViewIntoFile(string xafmlPath, XElement view)
    {
        var doc = File.Exists(xafmlPath) ? XDocument.Load(xafmlPath) : new XDocument(new XElement("Application"));
        var root = doc.Root ?? throw new InvalidOperationException($"{xafmlPath} has no root element.");
        var views = root.Element("Views");
        if (views == null)
        {
            views = new XElement("Views");
            InsertByKey(root, views);
        }
        var target = views.Elements().FirstOrDefault(e => Key(e) == Key(view));
        MergeNode(views, target, view, targetParentNew: false, insertNew: e => InsertByKey(views, e));

        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", NewLineChars = "\r\n", Encoding = new UTF8Encoding(true) };
        using var writer = XmlWriter.Create(xafmlPath, settings);
        doc.Save(writer);
    }

    // Mirrors ModelNode.MoveNode (DX 26.1, ModelNode.cs 3751-3807); source = user-layer node, target = module node or null.
    static void MergeNode(XElement parent, XElement? target, XElement source, bool targetParentNew, Action<XElement> insertNew)
    {
        bool srcNew = Flag(source, "IsNewNode"), srcRemoved = Flag(source, "Removed");
        bool tgtRemoved = target != null && Flag(target, "Removed");
        // The reader makes children of a New node New unless they are Removed; the file need not say so.
        bool tgtNew = target != null && (target.Attribute("IsNewNode") != null ? Flag(target, "IsNewNode") : !tgtRemoved && targetParentNew);

        if (srcRemoved && !srcNew)
        {
            if (target == null)
                insertNew(Stub(source, isNew: false, removed: true));
            else if (tgtNew && tgtRemoved)
                target.ReplaceWith(Stub(source, isNew: false, removed: true)); // deviation 1a: keep suppressing what is below
            else if (tgtNew)
                target.Remove();
            else
                target.SetAttributeValue("Removed", "True");
            return;
        }

        if (target == null)
        {
            target = Stub(source, isNew: srcNew, removed: srcRemoved); // deviation 1b: a replacement stays a replacement
            insertNew(target);
        }
        else if (target.Name != source.Name)
        {
            var replacement = Stub(source, isNew: true, removed: tgtRemoved || !tgtNew); // deviation 1c
            target.ReplaceWith(replacement);
            target = replacement;
        }
        else if (srcRemoved && srcNew)
        {
            var key = KeyAttribute(target);
            target.RemoveAll();
            if (key != null) target.SetAttributeValue(key.Name, key.Value);
            target.SetAttributeValue("Removed", tgtRemoved || !tgtNew ? "True" : null);
            target.SetAttributeValue("IsNewNode", "True");
            tgtNew = true;
        }
        else if (tgtRemoved && !tgtNew && srcNew)
        {
            target.SetAttributeValue("IsNewNode", "True");
            tgtNew = true;
        }

        foreach (var a in source.Attributes().Where(a => !IsKeyOrState(a)))
            target.SetAttributeValue(a.Name, a.Value);
        // Order: existing children keep their places; a new child goes after the counterpart of the source's
        // previous sibling, else last. The source was written by ModelXmlWriter, so its order is the writer's;
        // the file alone cannot reproduce that sort (it uses the composed Index, which is not in the file).
        var t = target;
        XElement? prev = null;
        foreach (var child in source.Elements())
        {
            var counterpart = t.Elements().FirstOrDefault(e => Key(e) == Key(child));
            var after = prev;
            MergeNode(t, counterpart, child, tgtNew, e => { if (after != null) after.AddAfterSelf(e); else t.Add(e); });
            prev = t.Elements().FirstOrDefault(e => Key(e) == Key(child)) ?? prev;
        }
    }

    static XElement Stub(XElement source, bool isNew, bool removed)
    {
        var e = new XElement(source.Name);
        var key = KeyAttribute(source);
        if (key != null) e.SetAttributeValue(key.Name, key.Value);
        if (isNew) e.SetAttributeValue("IsNewNode", "True");
        if (removed) e.SetAttributeValue("Removed", "True");
        return e;
    }

    // Key: the Id attribute, else the element name (property nodes like Layout, Items, Columns have no Id).
    static string Key(XElement e) => (string?)e.Attribute("Id") ?? e.Name.LocalName;
    static XAttribute? KeyAttribute(XElement e) => e.Attribute("Id");
    static bool IsKeyOrState(XAttribute a) => a.Name == "Id" || a.Name == "IsNewNode" || a.Name == "Removed";
    static bool Flag(XElement e, string name) => bool.TryParse((string?)e.Attribute(name), out var b) && b;

    // Top-level placement (Views children, and Views under Application) by key, the writer's tie-break order.
    static void InsertByKey(XElement parent, XElement child)
    {
        var next = parent.Elements().FirstOrDefault(e => Comparer<string>.Default.Compare(Key(e), Key(child)) > 0);
        if (next != null) next.AddBeforeSelf(child); else parent.Add(child);
    }
}
