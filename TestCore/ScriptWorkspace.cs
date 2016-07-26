using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System.Text.RegularExpressions;

public static class ScriptWorkspace
{

    public static async Task<Project> FromDirectoryScanAsync(string dir, CancellationToken cancel = default(CancellationToken))
    {
        Directory.CreateDirectory(dir + "/obj/replay");
        var projName = Path.GetFileName(dir);

        // Scrape all the #r nuget references out of the .csx file
        var nugetReferences = new List<Tuple<string, string>>();
        var reNugetReference = new Regex(@"^\s*#r\s+""([^"",]+)(,\s*([^""]+))?""\s*(//.*)?$");
        foreach (var file in Directory.GetFiles(dir, "*.csx"))
        {
            using (var stream = new StreamReader(File.OpenRead(file)))
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
                    var match = reNugetReference.Match(s);
                    if (!match.Success) continue;
                    var name = match.Groups[1].Value;
                    var version = match.Groups[3].Value;
                    nugetReferences.Add(Tuple.Create(name, version));
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
            if (!File.Exists(dir + "/project.json") || new FileInfo(dir + "/project.json").LastWriteTimeUtc < lastWrite)
            {
                pjson = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(dir + "/obj/replay/project.json"));
                var deps = pjson["dependencies"] as Newtonsoft.Json.Linq.JObject;
                if (nugetReferences.Any(nref => deps.Property(nref.Item1) == null)) pjson = null;
            }
        }
        if (pjson == null)
        {
            if (File.Exists(dir + "/project.json"))
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

        // Do we need to rebuild the project, so as to get project.lock.json and csc.rsp and other files?
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

        var workspace = new AdhocWorkspace();
        var projectInfo = CreateProjectInfoFromCommandLine(projName, LanguageNames.CSharp, File.ReadAllLines(dir + "/obj/replay/dotnet-compile-csc.rsp"), dir + "/obj/replay", workspace);
        var project = workspace.AddProject(projectInfo);

        project = project.AddAdditionalDocument("project.json", File.ReadAllText(dir + "/obj/replay/project.json"), null, "project.json").Project;
        foreach (var file in Directory.GetFiles(dir, "*.cs")) project = project.AddDocument(Path.GetFileName(file), File.ReadAllText(file), null, Path.GetFileName(file)).Project;
        foreach (var file in Directory.GetFiles(dir, "*.csx")) project = project.AddDocument(Path.GetFileName(file), File.ReadAllText(file), null, Path.GetFileName(file)).WithSourceCodeKind(SourceCodeKind.Script).Project;
        var reFence = new Regex("^( *)((?<back>````*)|(?<tilde>~~~~*)) *([^ \r\n]*)");
        foreach (var file in Directory.GetFiles(dir, "*.md"))
        {
            var csx = new StringBuilder();
            var lines = File.ReadAllLines(file);
            //
            bool isInCodeBlock = false; string terminator="", lang="", indent="", fence=""; int iline = 0;
            foreach (var line in lines)
            {
                iline++;
                if (!isInCodeBlock)
                {
                    var match = reFence.Match(line);
                    if (!match.Success) continue;
                    isInCodeBlock = true;
                    terminator = line.EndsWith("\r\n") ? "\r\n" : line.EndsWith("\r") ? "\r" : line.EndsWith("\n") ? "\n" : "\r\n";
                    indent = match.Groups[1].Value; fence = match.Groups[2].Value; lang = match.Groups[3].Value;
                    csx.Append($"#line {iline} \"{Path.GetFileName(file)}\"{terminator}");
                }
                else
                {
                    string line2 = line, indent2 = indent; while (indent2.StartsWith(" ") && line2.StartsWith(" ")) { line2 = line2.Substring(1); indent2 = indent2.Substring(1); }
                    if (line2.StartsWith(fence)) isInCodeBlock = false;
                    else if (reNugetReference.IsMatch(line)) csx.Append($"//{line}{terminator}");
                    else csx.Append($"{line}{terminator}");
                }
            }
            //
            var text = csx.ToString();
            project = project.AddDocument(Path.GetFileName(file), File.ReadAllText(file), null, Path.GetFileName(file)).Project;
            project = project.AddDocument(Path.GetFileName(file)+".csx", csx.ToString(), null, Path.GetFileName(file)+".csx").WithSourceCodeKind(SourceCodeKind.Script).Project;
        }

        return project;
    }


    class MyMetadataResolver : MetadataReferenceResolver
    {
        public override bool Equals(object other) => ReferenceEquals(this, other);
        public override int GetHashCode() => 0;
        public override ImmutableArray<PortableExecutableReference> ResolveReference(string reference, string baseFilePath, MetadataReferenceProperties properties)
        {
            if (!reference.EndsWith(".dll")) return ImmutableArray<PortableExecutableReference>.Empty;
            return ImmutableArray.Create(MetadataReference.CreateFromFile(reference, properties));
        }
    }

    class MyAnalyzerLoader : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath)
        {
        }

        public Assembly LoadFromPath(string fullPath)
        {
            // Try Assembly.LoadFrom(string), part of .NET Framework
            var loadFrom = typeof(Assembly).GetTypeInfo().GetMethod("LoadFrom", new[] { typeof(string) });
            if (loadFrom != null) return loadFrom.Invoke(null, new[] { fullPath }) as Assembly;

            // Try System.Runtime.Loader.ASsemblyLoadContext.LoadFromAssemblyPath, part of System.Runtime.Loader nuget package
            var assemblyLoadContextType = Type.GetType("System.Runtime.Loader.AssemblyLoadContext, System.Runtime.Loader");
            var currentLoadContext = assemblyLoadContextType.GetTypeInfo().GetProperty("Default").GetValue(null, null);
            return currentLoadContext.GetType().GetTypeInfo().GetMethod("LoadFromAssemblyPath").Invoke(currentLoadContext, new[] { fullPath }) as Assembly;
        }
    }

    class MyFileLoader : TextLoader
    {
        public string Path;
        public Encoding DefaultEncoding;

        protected virtual SourceText CreateText(Stream stream, Workspace workspace) => SourceText.From(stream, DefaultEncoding);
        public override Task<TextAndVersion> LoadTextAndVersionAsync(Workspace workspace, DocumentId documentId, CancellationToken cancellationToken) => Task.FromResult(TextAndVersion.Create(SourceText.From(File.ReadAllText(Path), DefaultEncoding), VersionStamp.Create(new FileInfo(Path).LastWriteTimeUtc), Path));
    }


    static ProjectInfo CreateProjectInfoFromCommandLine(string projectName, string language, IEnumerable<string> commandLineArgs, string projectDirectory, Workspace workspace)
    {
        var dll = typeof(Microsoft.CodeAnalysis.Host.ILanguageService).GetTypeInfo().Assembly;

        var languageServices = workspace.Services.GetLanguageServices(language);
        var commandLineParser = languageServices.GetType().GetTypeInfo().GetMethod("GetRequiredService").MakeGenericMethod(dll.GetType("Microsoft.CodeAnalysis.Host.ICommandLineParserService")).Invoke(languageServices, null);
        var commandLineArguments = commandLineParser.GetType().GetTypeInfo().GetMethod("Parse").Invoke(commandLineParser, new object[] { commandLineArgs, projectDirectory, false, null }) as CommandLineArguments;
        var myMetadataResolver = new MyMetadataResolver();
        var myAnalyzerLoader = new MyAnalyzerLoader();
        var xmlFileResolver = new XmlFileResolver(commandLineArguments.BaseDirectory);
        var strongNameProvider = new DesktopStrongNameProvider(commandLineArguments.KeyFileSearchPaths.Where(s => s != null).ToImmutableArray());

        var boundMetadataReferences = commandLineArguments.ResolveMetadataReferences(myMetadataResolver);
        var unresolvedMetadataReference = boundMetadataReferences.FirstOrDefault(r => r is UnresolvedMetadataReference);
        if (unresolvedMetadataReference != null) throw new Exception($"Can't resolve '{unresolvedMetadataReference.Display}'");
        foreach (var path in commandLineArguments.AnalyzerReferences.Select(r => r.FilePath)) myAnalyzerLoader.AddDependencyLocation(path);
        var boundAnalyzerReferences = commandLineArguments.ResolveAnalyzerReferences(myAnalyzerLoader);
        var unresolvedAnalyzerReference = boundAnalyzerReferences.FirstOrDefault(r => r is UnresolvedAnalyzerReference);
        if (unresolvedAnalyzerReference != null) throw new Exception($"Can't resolve '{unresolvedAnalyzerReference.Display}'");

        var assemblyIdentityComparer = DesktopAssemblyIdentityComparer.Default;
        if (commandLineArguments.AppConfigPath != null)
        {
            using (var appConfigStream = new FileStream(commandLineArguments.AppConfigPath, FileMode.Open, FileAccess.Read))
            {
                assemblyIdentityComparer = DesktopAssemblyIdentityComparer.LoadFromXml(appConfigStream);
            }
        }

        var projectId = ProjectId.CreateNewId(debugName: projectName);

        var docs = new List<DocumentInfo>();
        foreach (var file in commandLineArguments.SourceFiles)
        {
            if (!File.Exists(file.Path)) throw new Exception($"Can't find '{file}'");
            var path = Path.GetFullPath(file.Path);

            var doc = DocumentInfo.Create(
               id: DocumentId.CreateNewId(projectId, path),
               name: Path.GetFileName(path),
               folders: null,
               sourceCodeKind: file.IsScript ? SourceCodeKind.Script : SourceCodeKind.Regular,
               loader: new MyFileLoader { Path = path, DefaultEncoding = commandLineArguments.Encoding },
               filePath: path);

            docs.Add(doc);
        }

        var additionalDocs = new List<DocumentInfo>();
        foreach (var file in commandLineArguments.AdditionalFiles)
        {
            if (!File.Exists(file.Path)) throw new Exception($"Can't find '{file}'");
            var path = Path.GetFullPath(file.Path);

            var doc = DocumentInfo.Create(
               id: DocumentId.CreateNewId(projectId, path),
               name: Path.GetFileName(path),
               folders: null,
               sourceCodeKind: SourceCodeKind.Regular,
               loader: new MyFileLoader { Path = path, DefaultEncoding = commandLineArguments.Encoding },
               filePath: path);

            additionalDocs.Add(doc);
        }

        if (commandLineArguments.OutputFileName == null) throw new ArgumentNullException("OutputFileName");

        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            projectName,
            commandLineArguments.OutputFileName,
            language: language,
            filePath: Path.GetFullPath($"{projectDirectory}/project.json"),
            outputFilePath: Path.GetFullPath($"{projectDirectory}/{projectName}.dll"),
            compilationOptions: commandLineArguments.CompilationOptions
                .WithXmlReferenceResolver(xmlFileResolver)
                .WithAssemblyIdentityComparer(assemblyIdentityComparer)
                .WithStrongNameProvider(strongNameProvider)
                .WithMetadataReferenceResolver(myMetadataResolver),
            parseOptions: commandLineArguments.ParseOptions,
            documents: docs,
            additionalDocuments: additionalDocs,
            metadataReferences: boundMetadataReferences,
            analyzerReferences: boundAnalyzerReferences);

        return projectInfo;
    }



    static async Task<Tuple<string, string>> RunAsync(string fileName, string arguments, string workingDirectory, CancellationToken cancel = default(CancellationToken))
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

}
