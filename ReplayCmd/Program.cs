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
        TestScriptAsync().GetAwaiter().GetResult();
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
        var workspace = Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace.Create();
        var solution = await workspace.OpenSolutionAsync(@"C:\Users\ljw10\Documents\Visual Studio 2015\Projects\ConsoleApplicationCS\ConsoleApplicationCS.sln");
        var project = await ReplayHost.InstrumentProjectAsync(solution.Projects.Single());
        var document = project.Documents.FirstOrDefault(d => Path.GetFileName(d.FilePath) == "Program.cs");
        if (document != null) Console.WriteLine($"{document.FilePath}\r\n{await document.GetTextAsync()}");
        var result = await ReplayHost.BuildAsync(project);
        foreach (var d in result.Diagnostics) if (d.Severity == DiagnosticSeverity.Error || d.Severity == DiagnosticSeverity.Warning) Console.WriteLine($"{Path.GetFileName(d.Location.SourceTree.FilePath)}({d.Location.GetMappedLineSpan().StartLinePosition.Line}):{d.GetMessage()}");
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
