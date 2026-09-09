using System.Xml.Linq;
using XafMergerTool.Module.ModelMerge;

namespace XafMergerTool.E2E;

/// <summary>Marker checks for the node-by-node splice (docs/MERGE-003-PLAN.md, state table and deviations 1a-1c).</summary>
public class XafmlViewMergerTests
{
    // Module xafml: a hand-made group "Extra" (Notes inside it is New by inheritance, no attribute),
    // a generated group "Gen" the module replaced (both markers), and a caption on the generated Main group.
    const string Module = """
        <?xml version="1.0" encoding="utf-8"?>
        <Application Title="T">
          <Views>
            <ListView Id="ApplicationUser_ListView" Caption="Users" />
            <DetailView Id="Customer_DetailView">
              <Layout>
                <LayoutGroup Id="Main" Caption="Module caption">
                  <LayoutGroup Id="Extra" IsNewNode="True" Caption="Extra"><LayoutItem Id="Notes" /></LayoutGroup>
                  <LayoutGroup Id="Gen" Removed="True" IsNewNode="True" Caption="Replaced" />
                </LayoutGroup>
              </Layout>
            </DetailView>
          </Views>
        </Application>
        """;

    string path = "";

    [SetUp]
    public void WriteModule()
    {
        path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"merge-{TestContext.CurrentContext.Test.ID}.xafml");
        File.WriteAllText(path, Module);
    }

    static XElement Layer(string viewsInner) => XafmlViewMerger.FindView($"<Application><Views>{viewsInner}</Views></Application>", "Customer_DetailView")!;

    XElement Merge(string viewsInner)
    {
        XafmlViewMerger.MergeViewIntoFile(path, Layer(viewsInner));
        return XDocument.Load(path).Root!.Element("Views")!.Elements("DetailView").Single();
    }

    static XElement Node(XElement view, string id) => view.Descendants().Single(e => (string?)e.Attribute("Id") == id);

    [Test]
    public void MoveIntoModuleGroup_KeepsModuleMarkerAndUntouchedValues()
    {
        var view = Merge("""
            <DetailView Id="Customer_DetailView"><Layout><LayoutGroup Id="Main"><LayoutGroup Id="Extra">
              <LayoutItem Id="Street" Index="1" ViewItem="Street" IsNewNode="True" />
            </LayoutGroup></LayoutGroup></Layout></DetailView>
            """);
        Assert.That((string?)Node(view, "Extra").Attribute("IsNewNode"), Is.EqualTo("True"), "module's creation marker survives");
        Assert.That((string?)Node(view, "Extra").Attribute("Caption"), Is.EqualTo("Extra"), "untouched value stays");
        Assert.That((string?)Node(view, "Main").Attribute("Caption"), Is.EqualTo("Module caption"), "untouched sibling value stays");
        Assert.That(Node(view, "Extra").Elements().Select(e => (string?)e.Attribute("Id")), Is.EqualTo(new[] { "Notes", "Street" }), "existing children keep their place, new ones follow");
        Assert.That((string?)Node(view, "Street").Attribute("IsNewNode"), Is.EqualTo("True"));
    }

    [Test]
    public void RemoveInheritedNewChild_DeletesIt()
    {
        var view = Merge("""<DetailView Id="Customer_DetailView"><Layout><LayoutGroup Id="Main"><LayoutGroup Id="Extra"><LayoutItem Id="Notes" Removed="True" /></LayoutGroup></LayoutGroup></Layout></DetailView>""");
        Assert.That(view.Descendants().Any(e => (string?)e.Attribute("Id") == "Notes"), Is.False, "Notes was New by inheritance, so it is deleted, not tombstoned");
    }

    [Test]
    public void RemoveModuleReplacement_KeepsTombstone()
    {
        var view = Merge("""<DetailView Id="Customer_DetailView"><Layout><LayoutGroup Id="Main"><LayoutGroup Id="Gen" Removed="True" /></LayoutGroup></Layout></DetailView>""");
        var gen = Node(view, "Gen");
        Assert.That((string?)gen.Attribute("Removed"), Is.EqualTo("True"), "1a: suppression of the generated node stays");
        Assert.That(gen.Attribute("IsNewNode"), Is.Null);
        Assert.That(gen.Attribute("Caption"), Is.Null);
    }

    [Test]
    public void RemoveGeneratedNode_WritesTombstone()
    {
        var view = Merge("""<DetailView Id="Customer_DetailView"><Layout><LayoutGroup Id="Main"><LayoutItem Id="Phone" Removed="True" /></LayoutGroup></Layout></DetailView>""");
        Assert.That((string?)Node(view, "Phone").Attribute("Removed"), Is.EqualTo("True"));
    }

    [Test]
    public void RecreateAsDifferentType_ReplacesAndKeepsNewMarker()
    {
        var view = Merge("""<DetailView Id="Customer_DetailView"><Layout><LayoutGroup Id="Main"><TabbedGroup Id="Extra" Removed="True" IsNewNode="True" Caption="Tabs" /></LayoutGroup></Layout></DetailView>""");
        var extra = Node(view, "Extra");
        Assert.That(extra.Name.LocalName, Is.EqualTo("TabbedGroup"));
        Assert.That((string?)extra.Attribute("IsNewNode"), Is.EqualTo("True"));
        Assert.That(extra.Attribute("Removed"), Is.Null, "1c: the old node was the module's own creation, nothing below to suppress");
        Assert.That((string?)extra.Attribute("Caption"), Is.EqualTo("Tabs"));
        Assert.That(extra.Elements(), Is.Empty, "replacement discards the old subtree");
    }

    [Test]
    public void ReplaceGeneratedNodeWithNoModuleEntry_KeepsBothMarkers()
    {
        var view = Merge("""<DetailView Id="Customer_DetailView"><Layout><LayoutGroup Id="Main"><TabbedGroup Id="Main_col1" Removed="True" IsNewNode="True" /></LayoutGroup></Layout></DetailView>""");
        var col = Node(view, "Main_col1");
        Assert.That((string?)col.Attribute("Removed"), Is.EqualTo("True"), "1b");
        Assert.That((string?)col.Attribute("IsNewNode"), Is.EqualTo("True"), "1b");
    }

    [Test]
    public void RecreateModuleReplacementAsDifferentType_KeepsSuppression()
    {
        var view = Merge("""<DetailView Id="Customer_DetailView"><Layout><LayoutGroup Id="Main"><TabbedGroup Id="Gen" IsNewNode="True" /></LayoutGroup></Layout></DetailView>""");
        var gen = Node(view, "Gen");
        Assert.That(gen.Name.LocalName, Is.EqualTo("TabbedGroup"));
        Assert.That((string?)gen.Attribute("Removed"), Is.EqualTo("True"), "1c: the generated node below stays suppressed");
        Assert.That((string?)gen.Attribute("IsNewNode"), Is.EqualTo("True"));
    }

    [Test]
    public void NewChildren_FollowSourceOrder_EmptyIndexKept()
    {
        var view = Merge("""<DetailView Id="Customer_DetailView"><Layout><LayoutGroup Id="Main"><LayoutItem Id="Z" Index="0" IsNewNode="True" /><LayoutGroup Id="Extra" /><LayoutItem Id="A" Index="" IsNewNode="True" /></LayoutGroup></Layout></DetailView>""");
        Assert.That(Node(view, "Main").Elements().Select(e => (string?)e.Attribute("Id")), Is.EqualTo(new[] { "Extra", "A", "Gen", "Z" }),
            "Z has no previous sibling so it goes last; A follows Extra, its previous sibling's counterpart");
        Assert.That((string?)Node(view, "A").Attribute("Index"), Is.EqualTo(""), "explicit null override kept");
    }

    [Test]
    public void NewViewAndFileFormat()
    {
        XafmlViewMerger.MergeViewIntoFile(path, XafmlViewMerger.FindView("""<Application><Views><ListView Id="Order_ListView"><Columns><ColumnInfo Id="Number" Width="50" /></Columns></ListView></Views></Application>""", "Order_ListView")!);
        var ids = XDocument.Load(path).Root!.Element("Views")!.Elements().Select(v => (string?)v.Attribute("Id"));
        Assert.That(ids, Is.EqualTo(new[] { "ApplicationUser_ListView", "Customer_DetailView", "Order_ListView" }));
        Assert.That(XafmlViewMerger.FindView("<Application/>", "Nope"), Is.Null);
        Assert.That(File.ReadAllText(path), Does.StartWith("﻿<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Application"));
    }
}
