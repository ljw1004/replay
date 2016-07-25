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
        //TestHostAsync().GetAwaiter().GetResult();
        TestWorkspaceAsync().GetAwaiter().GetResult();
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

    static async Task TestHostAsync()
    {
        var workspace = new Microsoft.DotNet.ProjectModel.Workspaces.ProjectJsonWorkspace(SampleProjectsDirectory + "/ConsoleApp1");
        var solution = workspace.CurrentSolution;
        var project = solution.Projects.Single();
        var txt = "int x = 15;\r\nint y = x+2;\r\nSystem.Console.WriteLine(y);\r\n";
        var document = project.AddDocument("a.csx", txt, null, "c:\\a.csx").WithSourceCodeKind(SourceCodeKind.Script);
        project = document.Project;

        var host = new ReplayHost(true);
        host.DiagnosticChanged += (isAdd, tag, diagnostic, deferral, cancel) =>
        {
            if (diagnostic.Severity == DiagnosticSeverity.Warning || diagnostic.Severity == DiagnosticSeverity.Error)
            {
                if (isAdd) Console.WriteLine($"+D{tag}: {diagnostic.GetMessage()}");
                else Console.WriteLine($"-D{tag}");
            }
            deferral.SetResult(null);
        };
        host.AdornmentChanged += (isAdd, tag, file, line, content, deferral, cancel) =>
        {
            if (isAdd) Console.WriteLine($"+A{tag}: {Path.GetFileName(file)}({line}) {content}");
            else Console.WriteLine($"-A{tag}");
            deferral.SetResult(null);
        };
        host.Erred += (error, deferral, cancel) =>
        {
            Console.WriteLine(error);
            deferral.SetResult(null);
        };

        Console.WriteLine("PROJECT");
        await host.ChangeDocumentAsync(project, null, 0, 0, 0);
        Console.WriteLine("VIEW");
        await host.WatchAsync("c:\\a.csx", 0, 10);

        Console.WriteLine("CHANGE");
        //txt = "int x = 15;\r\nint y = x+3;\r\n\r\nSystem.Console.WriteLine(y);\r\n";
        //document = document.WithText(SourceText.From(txt));
        //project = document.Project;
        //await host.DocumentHasChangedAsync(project, "c:\\a.csx", 1, 1, 2);
        txt = "int x = 15;\r\nint y = x+2;d\r\nSystem.Console.WriteLine(y);\r\n";
        document = document.WithText(SourceText.From(txt));
        project = document.Project;
        await host.ChangeDocumentAsync(project, "c:\\a.csx", 1, 1, 1);


        Console.WriteLine("DONE");
    }


    static async Task TestWorkspaceAsync(CancellationToken cancel = default(CancellationToken))
    {
        await Task.Delay(0);
        var dir = Directory.GetCurrentDirectory();
        for (; dir != null; dir = Path.GetDirectoryName(dir)) if (Directory.Exists(dir + "/SampleProjects")) break;
        dir = Path.GetFullPath(dir + "/SampleProjects/Methods");
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException(dir);
        Directory.CreateDirectory(dir + "/obj/replay");
        var projName = "Methods";

        // Scrape all the #r nuget references out of the .csx file
        var nugetReferences = new List<Tuple<string,string>>();
        foreach (var file in Directory.GetFiles(dir,"*.csx"))
        {
            using (var stream = new StreamReader(file))
            {
                while (true)
                {
                    // Look for lines of the form "#r NugetName [Version] [// comment]" (where NugetName doesn't end with .dll)
                    // and keep NugetName + Version
                    // Stop looking once we find a line that's not "//", not "#", and not whitespace.
                    var s = await stream.ReadLineAsync();
                    if (s == null) break;
                    s = s.Trim();
                    if (!s.StartsWith("#") && !s.StartsWith("//") && s != "") break;
                    if (!s.StartsWith("#r ")) continue;
                    s = s.Substring(3).Trim();
                    int i = s.IndexOf("//");
                    if (i != -1) s = s.Substring(0, i - 1).Trim();
                    if (s.EndsWith(".dll", StringComparison.InvariantCultureIgnoreCase)) continue;
                    i = s.IndexOf(" ");
                    string v = null;
                    if (i != -1) { v = s.Substring(i).Trim(); s = s.Substring(0, i).Trim(); }
                    nugetReferences.Add(Tuple.Create(s,v));
                }
            }
        }

        // Check if we need to update project.json
        // * We'd like to use the one in <dir>/obj/replay/project.json
        // * But if it doesn't contain all nugetReferences, or if the one in <dir>/project.json is newer,
        //   then we need a new one
        Newtonsoft.Json.Linq.JObject pjson = null;
        if (File.Exists(dir + "/obj/replay/project.json"))
        {
            var lastWrite = new FileInfo(dir + "/obj/replay/project.json").LastWriteTimeUtc;
            if (!File.Exists(dir + "/project.json") || new FileInfo(dir+"/project.json").LastWriteTimeUtc < lastWrite)
            {
                pjson = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(dir + "/obj/replay/project.json"));
                var deps = pjson["dependencies"] as Newtonsoft.Json.Linq.JObject;
                if (nugetReferences.Any(nref => deps.Property(nref.Item1) == null)) pjson = null;
            }
        }
        if (pjson == null)
        {
            // we need to update it
            if (File.Exists(dir+"/project.json"))
            {
                pjson = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(dir + "/project.json"));
            }
            else
            {
                pjson = Newtonsoft.Json.Linq.JObject.Parse(@"{
                      ""version"": ""1.0.0-*"",
                      ""buildOptions"": {""emitEntryPoint"": true},
                      ""dependencies"": {""Microsoft.NETCore.App"": {""type"": ""platform"",""version"": ""1.0.0""},},
                      ""frameworks"": {""netcoreapp1.0"": {""imports"": [""dotnet5.6"", ""dnxcore50"", ""portable-net45+win8""]}}
                    }");
            }
            var deps = pjson["dependencies"] as Newtonsoft.Json.Linq.JObject;
            foreach (var nref in nugetReferences)
            {
                if (deps.Property(nref.Item1) == null) deps.Add(nref.Item1, nref.Item2);
            }
            File.WriteAllText(dir + "/obj/replay/project.json", pjson.ToString());
        }

        if (!File.Exists(dir + $"/obj/replay/dotnet-compile-csc.rsp")
            || !File.Exists(dir + $"/obj/replay/{projName}.deps.json")
            || !File.Exists(dir + $"/obj/replay/{projName}.runtimeconfig.dev.json")
            || !File.Exists(dir + $"/obj/replay/{projName}.runtimeconfig.json"))
        {
            File.WriteAllText(dir + "/obj/replay/dummy.cs", "class DummyProgram { static void Main() {} }");
            File.Delete(dir + "/obj/replay/project.lock.json");
            var tt1 = await RunAsync("dotnet.exe", "restore", dir + "/obj/replay", cancel);
            if (!File.Exists(dir + "/obj/replay/project.lock.json")) throw new Exception(tt1.Item1 + "\r\n" + tt1.Item2);

            File.Delete(dir + $"/obj/replay/dotnet-compile-csc.rsp");
            File.Delete(dir + $"/obj/replay/dotnet-compile.assemblyinfo.cs");
            File.Delete(dir + $"/obj/replay/{projName}.deps.json");
            File.Delete(dir + $"/obj/replay/{projName}.runtimeconfig.dev.json");
            File.Delete(dir + $"/obj/replay/{projName}.runtimeconfig.json");
            var tt2 = await RunAsync("dotnet.exe", "build", dir + "/obj/replay", cancel);
            File.Copy(dir + "/obj/replay/obj/Debug/netcoreapp1.0/dotnet-compile-csc.rsp", dir + "/obj/replay/dotnet-compile-csc.rsp");
            File.Copy(dir + "/obj/replay/obj/Debug/netcoreapp1.0/dotnet-compile.assemblyinfo.cs", dir + "/obj/replay/dotnet-compile.assemblyinfo.cs");
            File.Copy(dir + "/obj/replay/bin/Debug/netcoreapp1.0/replay.deps.json", dir + $"/obj/replay/{projName}.deps.json");
            File.Copy(dir + "/obj/replay/bin/Debug/netcoreapp1.0/replay.runtimeconfig.dev.json", dir + $"/obj/replay/{projName}.runtimeconfig.dev.json");
            File.Copy(dir + "/obj/replay/bin/Debug/netcoreapp1.0/replay.runtimeconfig.json", dir + $"/obj/replay/{projName}.runtimeconfig.json");

            var lines = File.ReadAllLines(dir + "/obj/replay/dotnet-compile-csc.rsp");
            lines = lines.Where(s => !s.EndsWith("dotnet-compile.assemblyinfo.cs\""))
                .Where(s => !s.StartsWith("-out"))
                .Where(s => !s.EndsWith("dummy.cs\""))
                .Concat(new[] { $"\"{dir}/obj/replay/dotnet-compile.assemblyinfo.cs\"" })
                .Concat(new[] { $"-out:\"{dir}/obj/replay/{projName}.replay.exe\"" })
                .ToArray();
            File.WriteAllLines(dir + "/obj/replay/dotnet-compile-csc.rsp", lines);

            Directory.Delete(dir + "/obj/replay/obj", true);
            Directory.Delete(dir + "/obj/replay/bin", true);
        }

        var workspace = new AdhocWorkspace(Microsoft.CodeAnalysis.Host.Mef.DesktopMefHostServices.DefaultServices);
        var projectInfo = CommandLineProject.CreateProjectInfo(projName, LanguageNames.CSharp, File.ReadAllText(dir + "/obj/replay/dotnet-compile-csc.rsp"), dir + "/obj/replay", workspace);
        var project = workspace.AddProject(projectInfo);

        foreach (var file in Directory.GetFiles(dir, "*.cs"))
        {
            project = project.AddDocument(Path.GetFileName(file), File.ReadAllText(file), null, file).Project;
        }
        var sb = new StringBuilder();
        foreach (var file in Directory.GetFiles(dir, "*.csx"))
        {
            var lines = File.ReadAllLines(file).Select(s => s.TrimStart().StartsWith("#r ") ? "// " + s : s);
            var text = string.Join("\r\n", lines);
            foreach (var line in lines) sb.AppendLine(line);
        }
        project = project.AddDocument("synthesized.csx", sb.ToString(), null, "synthesized.csx").WithSourceCodeKind(SourceCodeKind.Script).Project;

        var dd = project.GetCompilationAsync().Result.GetDiagnostics();
        foreach (var d in dd)
        {
            if (d.Severity != DiagnosticSeverity.Warning && d.Severity != DiagnosticSeverity.Error) continue;
            Console.WriteLine(d);
        }
        Console.WriteLine(sb.ToString());
        Console.WriteLine(pjson);


    }

    static async Task<Tuple<string,string>> RunAsync(string fileName, string arguments, string workingDirectory, CancellationToken cancel = default(CancellationToken))
    {
        using (var process = new System.Diagnostics.Process())
        {
            process.StartInfo.FileName = fileName;
            process.StartInfo.Arguments = arguments;
            process.StartInfo.WorkingDirectory = workingDirectory;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using (var reg = cancel.Register(process.Kill)) await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return Tuple.Create(stdout, stderr);
        }
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
