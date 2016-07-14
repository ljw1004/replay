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
    static string SampleProjectsDirectory;

    static void Main()
    {
        string d = Directory.GetCurrentDirectory();
        for (; !Directory.Exists(d + "/SampleProjects") && d != null; d = Path.GetDirectoryName(d)) { }
        if (d == null) throw new Exception("Sample projects directory not found");
        SampleProjectsDirectory = d + "/SampleProjects";

        //TestScriptInstrumentingAsync().GetAwaiter().GetResult();
        //TestClientAsync().GetAwaiter().GetResult();
        TestHostAsync().GetAwaiter().GetResult();
    }

    static async Task TestScriptInstrumentingAsync()
    {
        var workspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "TestProject", "TestProject", LanguageNames.CSharp);
        var project = workspace.AddProject(projectInfo);
        var src = SourceText.From(File.ReadAllText(@"C:\Users\ljw10\Documents\Visual Studio 2015\Projects\ScriptApplicationCS\CodeFile1.csx"));
        var document = workspace.AddDocument(project.Id, "CodeFile1.csx", src).WithSourceCodeKind(SourceCodeKind.Script);
        document = await ReplayHost.InstrumentDocumentAsync(document, CancellationToken.None);
        Console.WriteLine($"{document.FilePath}\r\n{await document.GetTextAsync()}");
    }

    static async Task TestHostAsync()
    {
        var workspace = new Microsoft.DotNet.ProjectModel.Workspaces.ProjectJsonWorkspace(SampleProjectsDirectory + "/ConsoleApp1");
        var solution = workspace.CurrentSolution;
        var project = solution.Projects.Single();
        var txt = "int x = 15;\r\nint y = x+2;\r\nSystem.Console.WriteLine(y);\r\n";
        var document = project.AddDocument("a.csx", txt, null, "c:\\a.csx").WithSourceCodeKind(SourceCodeKind.Script);
        project = document.Project;

        var host = new ReplayHost(true);
        host.OnDiagnosticChange += (isAdd, tag, diagnostic, deferral, cancel) =>
        {
            if (diagnostic.Severity == DiagnosticSeverity.Warning || diagnostic.Severity == DiagnosticSeverity.Error)
            {
                if (isAdd) Console.WriteLine($"+D{tag}: {diagnostic.GetMessage()}");
                else Console.WriteLine($"-D{tag}");
            }
            deferral.SetResult(null);
        };
        host.OnAdornmentChange += (isAdd, tag, line, content, deferral, cancel) =>
        {
            if (isAdd) Console.WriteLine($"+A{tag}: ({line}) {content}");
            else Console.WriteLine($"-A{tag}");
            deferral.SetResult(null);
        };
        host.OnError += (error, deferral, cancel) =>
        {
            Console.WriteLine(error);
            deferral.SetResult(null);
        };
        Console.WriteLine("PROJECT");
        await host.DocumentHasChangedAsync(project, null, 0, 0, 0);
        Console.WriteLine("VIEW");
        await host.ViewHasChangedAsync("c:\\a.csx", 0, 10);
        Console.WriteLine("CHANGE");
        txt = "int x = 15;\r\nint y = x+3;\r\n\r\nSystem.Console.WriteLine(y);\r\n";
        document = document.WithText(SourceText.From(txt));
        project = document.Project;
        await host.DocumentHasChangedAsync(project, "c:\\a.csx", 1, 1, 2);
        Console.WriteLine("DONE");
    }


    private static void Host_OnAdornmentChange(bool isAdd, int tag, int line, string content, TaskCompletionSource<object> deferral, CancellationToken cancel)
    {
        throw new NotImplementedException();
    }

    private static void Host_OnDiagnosticChange(bool isAdd, int tag, Diagnostic diagnostic, TaskCompletionSource<object> deferral, CancellationToken cancel)
    {
        throw new NotImplementedException();
    }

    static async Task TestClientAsync()
    {
        //var workspace = Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace.Create();
        //var solution = await workspace.OpenSolutionAsync(@"C:\Users\lwischik\Documents\Visual Studio 2015\Projects\ConsoleApplicationCS\ConsoleApplicationCS.sln");
        //var project = solution.Projects.Single();

        var workspace = new Microsoft.DotNet.ProjectModel.Workspaces.ProjectJsonWorkspace(SampleProjectsDirectory + "/ConsoleApp1");
        var solution = workspace.CurrentSolution;
        var project = solution.Projects.Single();
        var txt = "int x = 15;\r\nint y = x+2;\r\nSystem.Console.WriteLine(y);\r\n";
        project = project.AddDocument("a.csx", txt, null, "c:\\a.csx").WithSourceCodeKind(SourceCodeKind.Script).Project;

        project = await ReplayHost.InstrumentProjectAsync(project, CancellationToken.None);

        var document = project.Documents.FirstOrDefault(d => Path.GetFileName(d.FilePath) == "a.csx");
        if (document != null) Console.WriteLine($"{document.FilePath}\r\n{await document.GetTextAsync()}");
        var result = await ReplayHost.BuildAsync(project, CancellationToken.None);
        foreach (var d in result.Diagnostics)
        {
            if (d.Severity != DiagnosticSeverity.Error && d.Severity != DiagnosticSeverity.Warning) continue;
            var path = d.Location.IsInSource ? Path.GetFileName(d.Location.SourceTree.FilePath) : "";
            var line = d.Location.IsInSource ? d.Location.GetMappedLineSpan().StartLinePosition.Line.ToString() : "";
            Console.WriteLine($"{path}({line}):{d.GetMessage()}");
        }
        if (!result.Success) return;
        var process = await ReplayHost.LaunchProcessAsync(result.ReplayOutputFilePath, CancellationToken.None);
        var cts = new CancellationTokenSource();
        var task = Runner(process, cts.Token);
        var cmd = $"WATCH\t{document.FilePath}\t1\t40\t0";
        Console.WriteLine(cmd);
        await process.PostLineAsync(cmd, CancellationToken.None);
        while (true)
        {
            cmd = await Task.Run(Console.In.ReadLineAsync);
            if (cmd == null) break;
            await process.PostLineAsync(cmd, CancellationToken.None);
        }
        cts.Cancel();
        await task.IgnoreCancellation();
    }

    static async Task Runner(AsyncProcess host, CancellationToken cancel)
    {
        while (true)
        {
            var replay = await host.ReadLineAsync(cancel);
            if (replay == null) return;
            Console.WriteLine("< " + replay);
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
