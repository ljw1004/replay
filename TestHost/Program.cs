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

        //TestCodeInstrumentingAsync().GetAwaiter().GetResult();
        //TestScriptInstrumentingAsync().GetAwaiter().GetResult();
        //TestClientAsync().GetAwaiter().GetResult();
        //TestHostAsync("ConsoleApp1").GetAwaiter().GetResult();
        TestHostAsync("Methods").GetAwaiter().GetResult();
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

    static async Task TestCodeInstrumentingAsync()
    {
        var workspace = new AdhocWorkspace();
        var projectInfo = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "TestProject", "TestProject", LanguageNames.CSharp);
        var project = workspace.AddProject(projectInfo);
        var txt = @"
using System;

class Program
{
    static void Main()
    {
        Console.WriteLine(""hello"");
        int x = 2;
        Console.WriteLine(x);
    }
}
";
        var document = workspace.AddDocument(project.Id, "program.cs", SourceText.From(txt));
        document = await ReplayHost.InstrumentDocumentAsync(document, CancellationToken.None);
        Console.WriteLine($"{document.FilePath}\r\n{await document.GetTextAsync()}");
    }

    static async Task TestHostAsync(string projectName)
    {
        Project project;
        if (projectName == "ConsoleApp1")
        {
            var workspace = new Microsoft.DotNet.ProjectModel.Workspaces.ProjectJsonWorkspace(SampleProjectsDirectory + "/ConsoleApp1");
            var solution = workspace.CurrentSolution;
            project = solution.Projects.Single();
            var txt = "int x = 15;\r\nint y = x+2;\r\nSystem.Console.WriteLine(y);\r\n";
            var document = project.AddDocument("a.csx", txt, null, "c:\\a.csx").WithSourceCodeKind(SourceCodeKind.Script);
            project = document.Project;
        }
        else if (projectName == "Methods")
        {
            project = await ScriptWorkspace.FromDirectoryScanAsync(SampleProjectsDirectory + "/Methods");
        }
        else
        {
            throw new ArgumentException("Projects 'ConsoleApp1' and 'Methods' both work", nameof(projectName));
        }


        var host = new ReplayHost(false);
        host.DiagnosticChanged += (isAdd, tag, diagnostic, deferrable, cancel) =>
        {
            if (diagnostic.Severity == DiagnosticSeverity.Warning || diagnostic.Severity == DiagnosticSeverity.Error)
            {
                if (isAdd) Console.WriteLine($"+D{tag}: {diagnostic.GetMessage()}");
                else Console.WriteLine($"-D{tag}");
            }
        };
        host.AdornmentChanged += (isAdd, tag, file, line, content, deferrable, cancel) =>
        {
            if (isAdd) Console.WriteLine($"+A{tag}: {Path.GetFileName(file)}({line}) {content}");
            else Console.WriteLine($"-A{tag}");
        };
        host.Erred += (error, deferrable, cancel) =>
        {
            Console.WriteLine(error);
        };

        Console.WriteLine("PROJECT");
        await host.ChangeDocumentAsync(project, null, 0, 0, 0);
        Console.WriteLine("VIEW");
        await host.WatchAsync();

        if (projectName == "ConsoleApp1")
        {
            Console.WriteLine("CHANGE");
            var txt = "int x = 15;\r\nint y = x+2;d\r\nSystem.Console.WriteLine(y);\r\n";
            var document = project.Documents.First(d => d.Name == "a.csx");
            document = document.WithText(SourceText.From(txt));
            project = document.Project;
            await host.ChangeDocumentAsync(project, "a.csx", 1, 1, 1);
        }
        else if (projectName == "Methods")
        {
            Console.WriteLine("CHANGE MARKDOWN");
            var document = project.Documents.First(d => d.Name == "methods.md");
            var src = document.GetTextAsync().Result;
            var txt = src.ToString();
            int i = src.Lines.FindIndex(line => txt.Substring(line.Span.Start, line.Span.Length) == "Introductory prose");
            txt = txt.Replace("Introductory prose", "Some\nintroduction.");
            document = document.WithText(SourceText.From(txt));
            project = document.Project;
            //
            document = project.Documents.First(d => d.Name == "methods.md.csx");
            txt = ScriptWorkspace.Md2Csx("methods.md", txt);
            document = document.WithText(SourceText.From(txt));
            project = document.Project;
            await host.ChangeDocumentAsync(project, "methods.md", i, 1, 2);

            Console.WriteLine("VIEW");
            await host.WatchAsync();

            Console.WriteLine("CHANGE CODE");
            document = project.Documents.First(d => d.Name == "methods.md");
            src = document.GetTextAsync().Result;
            txt = src.ToString();
            i = src.Lines.FindIndex(line => txt.Substring(line.Span.Start, line.Span.Length) == "var txt = GetText();");
            txt = txt.Replace("var txt = GetText();", "var txt = GetText();\n");
            document = document.WithText(SourceText.From(txt));
            project = document.Project;
            //
            document = project.Documents.First(d => d.Name == "methods.md.csx");
            src = document.GetTextAsync().Result;
            txt = src.ToString();
            txt = txt.Replace("var txt = GetText();", "var txt = GetText();\n");
            document = document.WithText(SourceText.From(txt));
            project = document.Project;
            //
            await host.ChangeDocumentAsync(project, "methods.md", i, 1, 2);
        }


        Console.WriteLine("DONE");
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

    public static int FindIndex<T>(this IEnumerable<T> collection, Func<T,bool> predicate)
    {
        int i = 0;
        foreach (var element in collection)
        {
            if (predicate(element)) return i;
            i++;
        }
        return -1;
    }
}
