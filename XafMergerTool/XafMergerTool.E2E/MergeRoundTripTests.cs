using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;

namespace XafMergerTool.E2E;

/// <summary>
/// The one gate: move a field in the runtime layout designer, Merge To Module, rebuild + restart,
/// and the field position comes from the module xafml while the ModelDifference table no longer has it.
/// This test hosts the app itself because it has to restart it; it needs LocalDB and the source tree.
/// </summary>
[TestFixture]
public class MergeRoundTripTests : PageTest
{
    const string Url = "http://localhost:5000";
    const string ViewId = "Customer_DetailView";
    const string ConnStr = "Data Source=(localdb)\\mssqllocaldb;Integrated Security=SSPI;Initial Catalog=XafMergerTool";

    static readonly string Root = FindRoot();
    static readonly string ServerDir = Path.Combine(Root, "XafMergerTool.Blazor.Server");
    static readonly string ModuleXafml = Path.Combine(Root, "XafMergerTool.Module", "Model.DesignedDiffs.xafml");

    Process? app;
    string originalXafml = "";
    IPage page = null!;

    public override BrowserNewContextOptions ContextOptions() => new() { ViewportSize = new() { Width = 1600, Height = 1000 } };

    [SetUp]
    public async Task StartClean()
    {
        page = Page;
        originalXafml = File.ReadAllText(ModuleXafml);
        RemoveViewFromXafml();
        ClearUserLayer();
        await BuildAndStart();
    }

    [TearDown]
    public async Task StopAndRestore()
    {
        if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Failed)
        {
            var shot = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"{TestContext.CurrentContext.Test.Name}.png");
            try { await page.ScreenshotAsync(new() { Path = shot, FullPage = true }); TestContext.AddTestAttachment(shot); } catch { }
        }
        Stop();
        File.WriteAllText(ModuleXafml, originalXafml);
    }

    static readonly string[] DefaultCol2 = { "Postal Code", "City", "Country" };
    static readonly string[] StreetInCol2 = { "Postal Code", "City", "Street", "Country" };

    [Test]
    public async Task MoveField_Merge_Restart_LayoutComesFromModule()
    {
        await LoginAndOpenCustomer();
        Assert.That(await Column2Labels(), Is.EqualTo(DefaultCol2), "precondition: default layout");
        await MoveAndMerge("street", "country", StreetInCol2);

        var col2 = LayoutGroup("Customer_col2");
        Assert.That(col2.Elements("LayoutItem").Select(i => (string?)i.Attribute("Id")),
            Is.EqualTo(new[] { "PostalCode", "City", "Street", "Country" }), "module xafml has the layout node");
        Assert.That(UserLayerXml(), Does.Not.Contain(ViewId), "user layer no longer has the view after merge");

        await RestartAndLogin();
        Assert.That(await Column2Labels(), Is.EqualTo(StreetInCol2), "layout after restart comes from the module");
        Assert.That(UserLayerXml(), Does.Not.Contain(ViewId), "user layer still empty for the view after restart");
    }

    /// <summary>MERGE-003: the second merge lands on top of a module that already has a diff for the view.</summary>
    [Test]
    public async Task MoveFieldBack_SecondMergeMergesNodeByNode()
    {
        await LoginAndOpenCustomer();
        await MoveAndMerge("street", "country", StreetInCol2);
        await RestartAndLogin();
        Assert.That(await Column2Labels(), Is.EqualTo(StreetInCol2), "first merge in place");

        await MoveAndMerge("street", "name", DefaultCol2);

        Assert.That(LayoutGroup("Customer_col2").Elements("LayoutItem").Select(i => (string?)i.Attribute("Id")),
            Does.Not.Contain("Street"), "module's own creation in col2 is deleted, not tombstoned");
        var street = LayoutGroup("Customer_col1").Elements("LayoutItem").Single(i => (string?)i.Attribute("Id") == "Street");
        Assert.That((string?)street.Attribute("Removed"), Is.EqualTo("True"), "generated Street stays suppressed");
        Assert.That((string?)street.Attribute("IsNewNode"), Is.EqualTo("True"), "and is re-created by the module");
        Assert.That(UserLayerXml(), Does.Not.Contain(ViewId));
        // Kept as a test artifact for the manual Model Editor check.
        var artifact = Path.Combine(TestContext.CurrentContext.WorkDirectory, "twice-merged.xafml");
        File.Copy(ModuleXafml, artifact, overwrite: true);
        TestContext.AddTestAttachment(artifact);

        await RestartAndLogin();
        Assert.That(await Column2Labels(), Is.EqualTo(DefaultCol2), "layout after two merges composes to the default again");
        Assert.That(await Column1Labels(), Does.Contain("Street"));
    }

    async Task MoveAndMerge(string from, string to, string[] expectedCol2)
    {
        await OpenLayoutMenu();
        await page.Locator("[role=menuitem]:has-text('Customize Layout')").ClickAsync();
        await Expect(page.Locator(".main.design-mode")).ToBeVisibleAsync();
        await Expect(page.Locator(".xaf-layouteditor-menu")).ToBeVisibleAsync();
        await DragItem(from, to, expectedCol2);
        await page.Locator(".xaf-layouteditor-menu button[data-qa-selector='dx-popup-close-button']").ClickAsync();
        await Expect(page.Locator(".main.design-mode")).ToHaveCountAsync(0);
        Assert.That(await Column2Labels(), Is.EqualTo(expectedCol2), "designer applied the move");

        await page.Locator("[role=tab]:has-text('Tools')").ClickAsync();
        await XafButton("Merge To Module").ClickAsync();
        await Expect(page.GetByText($"Merged {ViewId}")).ToBeVisibleAsync();
    }

    async Task RestartAndLogin()
    {
        Stop();
        await BuildAndStart();
        // Fresh context: the old one keeps sockets to the killed process and script requests stall on them.
        page = await (await Browser.NewContextAsync(ContextOptions())).NewPageAsync();
        await LoginAndOpenCustomer();
    }

    static XElement LayoutGroup(string id) => XDocument.Load(ModuleXafml).Root!
        .Element("Views")!.Elements("DetailView").Single(v => (string?)v.Attribute("Id") == ViewId)
        .Descendants("LayoutGroup").Single(g => (string?)g.Attribute("Id") == id);

    async Task LoginAndOpenCustomer()
    {
        // Not NetworkIdle: the Blazor circuit's websocket keeps the network busy and the wait times out.
        await page.GotoAsync($"{Url}/Customer_ListView", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Expect(XafButton("Log In")).ToBeVisibleAsync();
        await page.Locator("input[type=text]").First.FillAsync("Admin");
        await XafButton("Log In").ClickAsync();
        await page.Locator("[role=gridcell]:has-text('Acme BV')").ClickAsync();
        await Expect(page.Locator("label.xaf-item-country")).ToBeVisibleAsync();
    }

    // The editor reacts to the drag only once its JS has rebuilt the layout tree, which nothing in the DOM
    // announces; so drag, give Blazor a moment, and retry while the column has not changed.
    async Task DragItem(string fromItem, string toItem, string[] expectedCol2)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await page.Locator($"dxbl-form-layout-item:has(label.xaf-item-{fromItem})")
                .DragToAsync(page.Locator($"dxbl-form-layout-item:has(label.xaf-item-{toItem})"));
            await Task.Delay(1000);
            if ((await Column2Labels()).SequenceEqual(expectedCol2)) return;
        }
        Assert.Fail("drag never moved the item");
    }

    // The layout editor menu needs a right-click on empty layout space. The two columns differ in height,
    // so the patch beside the taller column's last row, inside the shorter column, is always empty.
    async Task OpenLayoutMenu()
    {
        var pt = await page.EvaluateAsync<double[]>(
            "() => { const boxes = [...document.querySelectorAll('label.xaf-item-name, label.xaf-item-email, label.xaf-item-phone, label.xaf-item-street, label.xaf-item-postalcode, label.xaf-item-city, label.xaf-item-country')].map(l => l.getBoundingClientRect()).filter(b => b.width);" +
            " const mid = Math.min(...boxes.map(b => b.left)) + Math.max(...boxes.map(b => b.right)); " +
            " const left = boxes.filter(b => b.left + b.right < mid), right = boxes.filter(b => b.left + b.right >= mid);" +
            " const bottom = c => Math.max(...c.map(b => b.bottom));" +
            " const shorter = bottom(left) < bottom(right) ? left : right, taller = shorter === left ? right : left;" +
            " return [shorter[0].left + 300, bottom(taller) - 6]; }");
        // The editor's JS attaches its contextmenu listener after Blazor's first render, so retry until the menu shows.
        for (var attempt = 0; ; attempt++)
        {
            await page.Mouse.ClickAsync((float)pt[0], (float)pt[1], new() { Button = MouseButton.Right });
            try { await Expect(page.Locator("[role=menuitem]").First).ToBeVisibleAsync(new() { Timeout = 2000 }); return; }
            catch (PlaywrightException) when (attempt < 10) { }
        }
    }

    // XAF renders each toolbar button twice (one virtual copy for overflow measurement).
    ILocator XafButton(string caption) => page.Locator($"button[data-action-name='{caption}']:not([dxbl-virtual-el])");

    Task<string[]> Column1Labels() => ColumnLabels("Name");
    Task<string[]> Column2Labels() => ColumnLabels("Postal Code");
    Task<string[]> ColumnLabels(string anchor) => page.EvaluateAsync<string[]>(
        "a => [...document.querySelectorAll('dxbl-form-layout-group')]" +
        ".map(g => [...g.querySelectorAll(':scope > .dxbl-row > dxbl-form-layout-item label.dxbl-fl-cpt')].map(l => l.textContent.trim()))" +
        ".filter(x => x.includes(a))[0]", anchor);

    // ---- app lifecycle ----

    async Task BuildAndStart()
    {
        Run("dotnet", $"build \"{Path.Combine(ServerDir, "XafMergerTool.Blazor.Server.csproj")}\" -nologo -v q");
        var exe = Path.Combine(ServerDir, "bin", "Debug", "net10.0", "XafMergerTool.Blazor.Server.exe");
        var psi = new ProcessStartInfo(exe, $"--urls {Url}") { WorkingDirectory = ServerDir, UseShellExecute = false };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        app = Process.Start(psi)!;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (app.HasExited) throw new Exception($"App exited with {app.ExitCode} during startup.");
            try { if ((await http.GetAsync(Url)).IsSuccessStatusCode) return; } catch { }
            await Task.Delay(1000);
        }
        throw new TimeoutException("App did not answer on " + Url);
    }

    void Stop()
    {
        if (app == null || app.HasExited) return;
        app.Kill(entireProcessTree: true);
        app.WaitForExit();
        app = null;
    }

    static void Run(string file, string args)
    {
        var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, RedirectStandardOutput = true })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new Exception($"{file} {args} failed:\n{output}");
    }

    // ---- state reset ----

    static void RemoveViewFromXafml()
    {
        var doc = XDocument.Load(ModuleXafml);
        doc.Root!.Element("Views")?.Elements().Where(e => (string?)e.Attribute("Id") == ViewId).Remove();
        doc.Save(ModuleXafml);
    }

    static void ClearUserLayer()
    {
        try
        {
            using var con = new SqlConnection(ConnStr);
            con.Open();
            using var cmd = new SqlCommand("DELETE FROM ModelDifferenceAspects; DELETE FROM ModelDifferences;", con);
            cmd.ExecuteNonQuery();
        }
        catch (SqlException) { /* first run: database does not exist yet */ }
    }

    static string UserLayerXml()
    {
        using var con = new SqlConnection(ConnStr);
        con.Open();
        using var cmd = new SqlCommand("SELECT ISNULL(STRING_AGG(CAST(Xml AS nvarchar(max)), ''), '') FROM ModelDifferenceAspects", con);
        return (string)cmd.ExecuteScalar()!;
    }

    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "XafMergerTool.Blazor.Server", "XafMergerTool.Blazor.Server.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Solution folder not found above " + AppContext.BaseDirectory);
    }
}
