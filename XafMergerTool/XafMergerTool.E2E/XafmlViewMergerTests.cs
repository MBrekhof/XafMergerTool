using System.Xml.Linq;
using XafMergerTool.Module.ModelMerge;

namespace XafMergerTool.E2E;

/// <summary>The one unit check for the XML splice: same-Id replaced, new view inserted in Id order, markers kept.</summary>
public class XafmlViewMergerTests
{
    [Test]
    public void ReplacesSameIdAndInsertsInIdOrder()
    {
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "merge-test.xafml");
        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <Application Title="T">
              <Views>
                <ListView Id="ApplicationUser_ListView" Caption="Users" />
                <DetailView Id="Customer_DetailView"><Layout><LayoutGroup Id="Main" Caption="old" /></Layout></DetailView>
              </Views>
            </Application>
            """);
        var layer = """
            <Application>
              <Views>
                <DetailView Id="Customer_DetailView"><Layout><LayoutItem Id="Street" Removed="True" /><LayoutItem Id="X" IsNewNode="True" /></Layout></DetailView>
                <ListView Id="Order_ListView"><Columns><ColumnInfo Id="Number" Width="50" /></Columns></ListView>
              </Views>
            </Application>
            """;

        XafmlViewMerger.MergeViewIntoFile(path, XafmlViewMerger.FindView(layer, "Customer_DetailView")!);
        XafmlViewMerger.MergeViewIntoFile(path, XafmlViewMerger.FindView(layer, "Order_ListView")!);

        var views = XDocument.Load(path).Root!.Element("Views")!.Elements().ToList();
        Assert.That(views.Select(v => (string?)v.Attribute("Id")),
            Is.EqualTo(new[] { "ApplicationUser_ListView", "Customer_DetailView", "Order_ListView" }));
        var detail = views[1];
        Assert.That(detail.Descendants("LayoutGroup"), Is.Empty, "old diff dropped: last write wins");
        Assert.That((string?)detail.Descendants("LayoutItem").First().Attribute("Removed"), Is.EqualTo("True"));
        Assert.That((string?)detail.Descendants("LayoutItem").Last().Attribute("IsNewNode"), Is.EqualTo("True"));
        Assert.That(XafmlViewMerger.FindView(layer, "Nope_DetailView"), Is.Null);
        Assert.That(File.ReadAllText(path), Does.StartWith("﻿<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Application"));
    }
}
