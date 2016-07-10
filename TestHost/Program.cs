using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.IO;
using System.Threading;
using Microsoft.CodeAnalysis.Text;

class Program
{
    static void Main()
    {
        //TestProjectAsync().GetAwaiter().GetResult();

        var workspace = new Microsoft.DotNet.ProjectModel.Workspaces.ProjectJsonWorkspace(@"C:\Users\lwischik\Documents\Visual Studio 2015\Projects\ConsoleApp1");
        var solution = workspace.CurrentSolution; 
        var project = solution.Projects.Single();
        project = project.AddDocument("a.csx", "").WithSourceCodeKind(SourceCodeKind.Script).Project;
        var comp = project.GetCompilationAsync().Result;
        foreach (var d in comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
            Console.WriteLine(d.GetMessage());
    }

    static async Task TestScriptAsync()
    {
        var workspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "TestProject", "TestProject", LanguageNames.CSharp);
        var project = workspace.AddProject(projectInfo);
        var src = SourceText.From(File.ReadAllText(@"C:\Users\ljw10\Documents\Visual Studio 2015\Projects\ScriptApplicationCS\CodeFile1.csx"));
        var document = workspace.AddDocument(project.Id, "CodeFile1.csx", src).WithSourceCodeKind(SourceCodeKind.Script);
        document = await ReplayHost.InstrumentDocumentAsync(document);
        Console.WriteLine($"{document.FilePath}\r\n{await document.GetTextAsync()}");
    }

    static async Task TestProjectAsync()
    {
        //var workspace = Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace.Create();
        //var solution = await workspace.OpenSolutionAsync(@"C:\Users\lwischik\Documents\Visual Studio 2015\Projects\ConsoleApplicationCS\ConsoleApplicationCS.sln");
        //var project = solution.Projects.Single();

        var workspace = new Microsoft.DotNet.ProjectModel.Workspaces.ProjectJsonWorkspace(@"C:\Users\lwischik\Documents\Visual Studio 2015\Projects\ConsoleApp1");
        var solution = workspace.CurrentSolution;
        var project = solution.Projects.Single();

        var txt = "int x = 15;\r\nint y = x+2;\r\nSystem.Console.WriteLine(y);\r\n";
        project = project.AddDocument("a.csx", txt, null, "c:\\a.csx").WithSourceCodeKind(SourceCodeKind.Script).Project;

        project = await ReplayHost.InstrumentProjectAsync(project);

        var document = project.Documents.FirstOrDefault(d => Path.GetFileName(d.FilePath) == "a.csx");
        if (document != null) Console.WriteLine($"{document.FilePath}\r\n{await document.GetTextAsync()}");
        var result = await ReplayHost.BuildAsync(project);
        foreach (var d in result.Diagnostics)
        {
            if (d.Severity != DiagnosticSeverity.Error && d.Severity != DiagnosticSeverity.Warning) continue;
            var path = d.Location.IsInSource ? Path.GetFileName(d.Location.SourceTree.FilePath) : "";
            var line = d.Location.IsInSource ? d.Location.GetMappedLineSpan().StartLinePosition.Line.ToString() : "";
            Console.WriteLine($"{path}({line}):{d.GetMessage()}");
        }
        if (!result.Success) return;
        var host = await ReplayHost.RunAsync(result.ReplayOutputFilePath);
        var cts = new CancellationTokenSource();
        var task = Runner(host, cts.Token);
        Console.WriteLine($"> watch {document.FilePath} 1 40");
        host.WatchAndMissing(document.FilePath, 1, 40,null);
        while (true)
        {
            var cmd = await Task.Run(Console.In.ReadLineAsync);
            if (string.IsNullOrEmpty(cmd)) break;
            Console.WriteLine("client unrecognized " + cmd);
        }
        cts.Cancel();
        await task.IgnoreCancellation();
    }

    static async Task Runner(ReplayHost host, CancellationToken cancel)
    {
        while (true)
        {
            var replay = await host.ReadReplayAsync(cancel);
            if (replay == null) return;
            Console.WriteLine($"{replay.Item1}:{replay.Item2}");
        }
    }

}

static class Extensions
{
    public static async Task IgnoreCancellation(this Task task)
    {
        try { await task; } catch (OperationCanceledException) { }
    }

    public static async Task<T> IgnoreCancellation<T>(this Task<T> task) where T:class
    {
        try { return await task; } catch (OperationCanceledException) { return null; }
    }
}
