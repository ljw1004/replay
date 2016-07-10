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
        TestProjectAsync().GetAwaiter().GetResult();
    }

    static async Task TestProjectAsync()
    {
        var workspace = new Microsoft.DotNet.ProjectModel.Workspaces.ProjectJsonWorkspace(@"C:\Users\lwischik\Documents\Visual Studio 2015\Projects\ConsoleApp1");
        var solution = workspace.CurrentSolution;
        var project = solution.Projects.Single();

        foreach (var fn in Directory.GetFiles(Path.GetDirectoryName(project.FilePath), "*.csx"))
        {
            project = project.AddDocument(Path.GetFileName(fn), File.ReadAllText(fn), null, fn).WithSourceCodeKind(SourceCodeKind.Script).Project;
        }
        project = await ReplayHost.InstrumentProjectAsync(project);
        
        var document = project.Documents.FirstOrDefault(d => Path.GetExtension(d.FilePath).ToLower() == ".csx");
        if (document == null) document = project.Documents.FirstOrDefault(d => Path.GetFileName(d.FilePath).ToLower().StartsWith("program"));
        if (document == null) document = project.Documents.FirstOrDefault();
        if (document == null) { Console.WriteLine("No documents found in project"); return; }
        Console.WriteLine($"{document.FilePath}\r\n{await document.GetTextAsync()}");

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
        Console.WriteLine($"watch {document.FilePath} 1 40");
        host.WatchAndMissing(document.FilePath, 1, 40, null);
        while (true)
        {
            Console.WriteLine();
            var cmd = await Task.Run(Console.In.ReadLineAsync);
            if (cmd == "quit" || cmd == "exit") break;
            if (string.IsNullOrEmpty(cmd)) continue;
            host.SendRawCommand(cmd);
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

    public static async Task<T> IgnoreCancellation<T>(this Task<T> task) where T : class
    {
        try { return await task; } catch (OperationCanceledException) { return null; }
    }
}
