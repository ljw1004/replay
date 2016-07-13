﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

// Client <-> Host <-> Editor
// The host is always on the same machine as the replayer. They communicate by serialization (to ensure clean teardown)
// The host and editor are in the same process (VSIX) or on remote machines (online).
// The host can trigger the client to shut down and a new client to launch.

// DESIGN CONSIDERATIONS
// Scenario: You have a file with replays shown, and you press "enter" to insert a line.
//           Should the adornments be hidden until the file is re-executed? -- no.
// Scenario: You have a file with replays shown, and you press "enter" which breaks a line and prevents building.
//           Should the adornments be hidden until the error is fixed? -- no.
// These get to the question of *when does an adornment get removed?*. The answer is, when you edit a line the adornment
// gets removed, and it doesn't get restored until the line of code re-runs.
// Scenario: You have an invocation fred() where fred is a non-logging helper method whose effect is to write to the console, so
//           that console write is shown at the invocation. Then you edit the helper method
//           to no longer print. When does the annotation at the invocation get removed? -- only
//           when the execution is guaranteed finished.

// WHO KNOWS WHAT
// Client: Each successive client instance builds up a database of logs that it has executed so far.
//         It also knows what range the host is currently watching, and the hashes the host has within that range.
//   Host: It has a persistent database of adornments (log + synthesized ID) for each one.
//         It also knows what range the editor is currently watching.
// Editor: It has a persistent database of adornments.

// CLIENT-INITIATED EVENTS
// * When client executes a new log, it adds to the database. If the log is within the host's watched range
//   then client notifies the host.
//   < REPLAY add line hash text
//   The host wil synthesize an ID, modify its database, and notifies the editor.
//   < REPLAY add line id text
//   The editor will modify its database and display onscreen.
// * When client reaches the end of its execution, it will send "removes" for anything in the watcher
//   database which isn't in the client database.
//   < REPLAY remove line
//   The host will remove them from its database, and pass them on to the editor.
//   < REPLAY remove line id
//   The editor will remove them from its database and screen

// EDITOR-INITIATED EVENTS
// * When editor has a text-change, this (1) removes affected adornments from screen and database, (2) shifts following
//   adornments up or down a line, (3) notifies the host.
//   > CHANGE file offset length text
//   The host will remove affected adornments from database, shift following adornments, update its Document model,
//   tear down the current client, launch a new client, and tell it the watch range plus what's in its database.
//   > WATCH file line count nhashes line0 hash0 ... lineN hashN
//   The cient will update its notion of what range the host is watching and what the host knows, and will send any
//   additions/modifications it sees fit (but no removals)
//   < REPLAY add line hash text
//   The host will respond to this as above.
// * When editor scrolls more lines into view, this (1) puts adornments onscreen based on what's in the editor's database,
//   (2) notifies the host of the new range.
//   > WATCH file line count
//   The host will notify the client of the new range also telling the client what things are in the editor's database
//   that weren't already known by the client
//   > WATCH file line count nhashes ...
//   The client will send add/modify notifications as needed (but no removals)
//   < REPLAY add line hash text
//   The host will respond to this as above.

// MISC EVENTS
// * The client also recognizes a debugging command
//   > DUMP
//   It responds with a complete dump of its database
//   < DUMP file line text
// * The client also recognizes another debugging command
//   > FILES
//   It responds with a list of its files
//   < FILE file


class ReplayHost : IDisposable
{
    private BufferBlock<Tuple<string, Project>> Queue = new BufferBlock<Tuple<string, Project>>();

    public delegate void AdornmentChangedHandler(bool isAdd, int id, int line, string content, TaskCompletionSource<object> deferral);
    public delegate void ReplayHostError(string error, TaskCompletionSource<object> deferral);
    public event AdornmentChangedHandler OnAdornmentChange;
    public event ReplayHostError OnError;

    public void DocumentHasChanged(Project project, string file, int line, int count, int newcount)
    {
        if (project == null) throw new ArgumentNullException(nameof(project));
        if (file == null) throw new ArgumentNullException(nameof(file));
        Queue.Post(Tuple.Create($"CHANGE\t{file}\t{line}\t{count}\t{newcount}", project));
    }
    public void ViewHasChanged(string file, int line, int count)
    {
        if (file == null) throw new ArgumentNullException(nameof(file));
        Queue.Post(Tuple.Create($"WATCH\t{file}\t{line}\t{count}", (Project)null));
    }

    private Task SendAdornmentChangeAsync(bool isAdd, int id, int line, string content)
    {
        var c = OnAdornmentChange;
        if (c == null) return Task.FromResult(0);
        var tcs = new TaskCompletionSource<object>();
        c.Invoke(isAdd, id, line, content, tcs);
        return tcs.Task;
    }

    private Task SendErrorAsync(string error)
    {
        var c = OnError;
        if (c == null) return Task.FromResult(0);
        var tcs = new TaskCompletionSource<object>();
        c.Invoke(error, tcs);
        return tcs.Task;
    }

    struct Adornment
    {
        public string File;
        public int Line;
        public string Content;
        public int ContentHash;
        public int Tag;
    }

    public async void RunAsync()
    {
        // This is the state of the client
        var Database = new Dictionary<string, Dictionary<int, Adornment>>();
        string watchFile = null;
        int watchLine = -1, watchCount = -1;
        CancellationTokenSource getProcessCancel = null;
        Task<AsyncProcess> getProcessTask = null;


        var queueTask = Queue.ReceiveAsync();
        var readProcessTask = null as Task<string>;
        

        while (true)
        {
            var tasks = new List<Task>(3);
            tasks.Add(queueTask);
            if (readProcessTask == null && getProcessTask != null) tasks.Add(getProcessTask);
            if (readProcessTask != null) tasks.Add(readProcessTask);
            await Task.WhenAny(tasks);

            if (getProcessTask.Status == TaskStatus.RanToCompletion && readProcessTask == null)
            {
                readProcessTask = (await getProcessTask).ReadLineAsync();
                continue;
            }

            if (queueTask.Status == TaskStatus.RanToCompletion && (await queueTask).Item1.StartsWith("CHANGE\t"))
            {
                var cmd = await queueTask; queueTask = Queue.ReceiveAsync();
                var cmdproject = cmd.Item2;
                var cmds = cmd.Item1.Split(new[] { '\t' });
                string file = cmds[1];
                int line = int.Parse(cmds[2]), count = int.Parse(cmds[3]), newcount = int.Parse(cmds[4]);
                //
                // TODO: modify the database
                //
                getProcessCancel?.Cancel();
                getProcessCancel = new CancellationTokenSource();
                getProcessTask = GetProcessAsync(cmdproject, getProcessTask, getProcessCancel.Token);
                readProcessTask = null;
                continue;
            }

            if (queueTask.Status == TaskStatus.RanToCompletion && (await queueTask).Item1.StartsWith("WATCH\t"))
            {
                var cmd = await queueTask; queueTask = Queue.ReceiveAsync();
                var cmds = cmd.Item1.Split(new[] { '\t' });
                string file = cmds[1];
                int line = int.Parse(cmds[2]), count = int.Parse(cmds[3]);
                //
                watchFile = file; watchLine = line; watchCount = count;
                if (getProcessTask?.Status != TaskStatus.RanToCompletion) continue;
                var process = await getProcessTask;
                await process.PostLineAsync("WATCH"); // TODO: fill this out
                continue;
            }

            if (queueTask.Status == TaskStatus.RanToCompletion)
            {
                // TODO error! we only expect to hear CHANGE and WATCH.
            }

            if (readProcessTask?.Status == TaskStatus.RanToCompletion && (await readProcessTask).StartsWith("REPLAY\t"))
            {
                var cmd = await readProcessTask;
                readProcessTask = (getProcessTask.Status == TaskStatus.RanToCompletion) ? (await getProcessTask).ReadLineAsync() : null;
                continue;
            }

            if (readProcessTask?.Status == TaskStatus.RanToCompletion)
            {
                var cmd = await readProcessTask;
                readProcessTask = (getProcessTask.Status == TaskStatus.RanToCompletion) ? (await getProcessTask).ReadLineAsync() : null;
                continue;
            }

            // TODO: what if any of the three things ended in failure?
        }

    }

    //CancellationTokenSource Cancel;
    //Task Task;
    ////
    //ImmutableArray<Tuple<int, Diagnostic>> CurrentClientDiagnostics = ImmutableArray<Tuple<int, Diagnostic>>.Empty;
    //public event Action<string> DiagnosticChanged;
    //static int DiagnosticCount;
    ////
    //string WatchFile; int WatchLine, WatchLineCount; ImmutableArray<int> WatchMissing;
    //TaskCompletionSource<object> WatchChanged;
    //public event Action<int, string> LineChanged;

    //class IntDiagnosticComparer : IEqualityComparer<Tuple<int, Diagnostic>>
    //{
    //    public string ToString(Diagnostic diagnostic)
    //    {
    //        var file = "";
    //        int offset = -1, length = -1;
    //        if (diagnostic.Location.IsInSource) { file = diagnostic.Location.SourceTree.FilePath; offset = diagnostic.Location.SourceSpan.Start; length = diagnostic.Location.SourceSpan.Length; }
    //        var dmsg = $"{diagnostic.Id}: {diagnostic.GetMessage()}";
    //        return $"{diagnostic.Severity}\t{file}\t{offset}\t{length}\t{dmsg}";
    //    }
    //    public bool Equals(Tuple<int, Diagnostic> x, Tuple<int, Diagnostic> y) => ToString(x.Item2) == ToString(y.Item2);
    //    public int GetHashCode(Tuple<int, Diagnostic> obj) => ToString(obj.Item2)?.GetHashCode() ?? 0;
    //}
    //static readonly IntDiagnosticComparer Comparer = new IntDiagnosticComparer();


    async Task<AsyncProcess> GetProcessAsync(Project project, Task<AsyncProcess> prevTask, CancellationToken cancel)
    {
        AsyncProcess prevProcess = null;
        if (prevTask != null) try { prevProcess = await prevTask.ConfigureAwait(false); } catch (Exception) { }
        if (prevProcess != null) await prevProcess.DisposeAsync();


        if (project == null) return;

        project = await ReplayHost.InstrumentProjectAsync(project, cancel).ConfigureAwait(false);
        var results = await ReplayHost.BuildAsync(project, cancel).ConfigureAwait(false);

        // Update warnings+errors
        var diagnostics = results.Diagnostics.Select(d => Tuple.Create(DiagnosticCount++, d)).ToImmutableArray();
        var toRemove = CurrentClientDiagnostics.Except(diagnostics, Comparer).ToImmutableArray();
        var toAdd = diagnostics.Except(CurrentClientDiagnostics, Comparer).ToImmutableArray();
        foreach (var id in CurrentClientDiagnostics.Except(diagnostics, Comparer)) DiagnosticChanged?.Invoke($"DIAGNOSTIC\tremove\t{id.Item1}\t{Comparer.ToString(id.Item2)}");
        foreach (var id in diagnostics.Except(CurrentClientDiagnostics, Comparer)) DiagnosticChanged?.Invoke($"DIAGNOSTIC\tadd\t{id.Item1}\t{Comparer.ToString(id.Item2)}");
        CurrentClientDiagnostics = diagnostics;
        if (!results.Success) return;

        var host = await ReplayHost.RunAsync(results.ReplayOutputFilePath, cancel).ConfigureAwait(false);
        if (WatchLineCount != 0) host.WatchAndMissing(WatchFile, WatchLine, WatchLineCount, null);
        WatchChanged = new TaskCompletionSource<object>();
        var replayTask = host.ReadReplayAsync(cancel);
        while (true)
        {
            await Task.WhenAny(WatchChanged.Task, replayTask).ConfigureAwait(false);
            if (WatchChanged.Task.IsCompleted)
            {
                host.WatchAndMissing(WatchFile, WatchLine, WatchLineCount, WatchMissing);
                WatchChanged = new TaskCompletionSource<object>();
            }
            else if (replayTask.IsCompleted)
            {
                var replay = await replayTask.ConfigureAwait(false);
                if (replay == null) return;
                LineChanged?.Invoke(replay.Item1, replay.Item2);
                replayTask = host.ReadReplayAsync(cancel);
            }
        }
    }

    public void Dispose()
    {
        Cancel?.Cancel();
    }



    public static async Task<Project> InstrumentProjectAsync(Project originalProject, CancellationToken cancel = default(CancellationToken))
    {
        var project = originalProject;

        var originalComp = await originalProject.GetCompilationAsync(cancel);
        var replay = originalComp.GetTypeByMetadataName("System.Runtime.CompilerServices.Replay");
        if (replay == null)
        {
            var fn = typeof(ReplayHost).GetTypeInfo().Assembly.Location;
            for (; fn != null; fn = Path.GetDirectoryName(fn))
            {
                if (File.Exists(fn + "/ReplayClient.cs")) break;
            }
            if (fn == null) throw new Exception("class 'Replay' not found");
            fn = fn + "/ReplayClient.cs";
            var document = project.AddDocument("ReplayClient.cs", File.ReadAllText(fn), null, fn);
            project = document.Project;
        }

        foreach (var documentId in originalProject.DocumentIds)
        {
            var document = await InstrumentDocumentAsync(project.GetDocument(documentId)).ConfigureAwait(false);
            project = document.Project;
        }
        return project;
    }

    public static async Task<Document> InstrumentDocumentAsync(Document document, CancellationToken cancel = default(CancellationToken))
    {
        var oldComp = await document.Project.GetCompilationAsync(cancel).ConfigureAwait(false);
        var oldTree = await document.GetSyntaxTreeAsync(cancel).ConfigureAwait(false);
        var oldRoot = await oldTree.GetRootAsync(cancel).ConfigureAwait(false);
        var rewriter = new TreeRewriter(oldComp, oldTree);
        var newRoot = rewriter.Visit(oldRoot);
        return document.WithSyntaxRoot(newRoot);
    }

    public class BuildResult
    {
        public BuildResult(EmitResult emitResult, string outputFilePath)
        {
            Success = emitResult.Success;
            Diagnostics = emitResult.Diagnostics;
            ReplayOutputFilePath = outputFilePath;
        }

        public readonly bool Success;
        public readonly ImmutableArray<Diagnostic> Diagnostics;
        public readonly string ReplayOutputFilePath;
    }

    public static async Task<BuildResult> BuildAsync(Project project, CancellationToken cancel = default(CancellationToken))
    {
        string fn = "";

        if (project.OutputFilePath != null)
        {
            // is a regular app
            for (int i = 0; ; i++)
            {
                fn = Path.ChangeExtension(project.OutputFilePath, $".replay{(i == 0 ? "" : i.ToString())}.exe");
                if (File.Exists(fn)) try { File.Delete(fn); } catch (Exception) { }
                if (!File.Exists(fn)) break;
            }
        }
        else
        {
            // is .NETCore app
            // (1) if directory "bin/debug/netcoreapp1.0" doesn't exist then create it by doing "dotnet build"
            var dir = Path.GetDirectoryName(project.FilePath);
            var bindir = dir + "/bin/Debug/netcoreapp1.0";
            if (!Directory.Exists(bindir))
            {
                using (var process = new Process())
                {
                    process.StartInfo.FileName = "dotnet.exe";
                    process.StartInfo.Arguments = "build";
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                    var opTask = process.StandardOutput.ReadToEndAsync();
                    using (var reg = cancel.Register(process.Kill)) await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
                    cancel.ThrowIfCancellationRequested();
                    var op = await opTask;
                    if (!Directory.Exists(bindir)) throw new Exception("Failed to dotnet build - " + op);
                }
            }

            // (2) pick a directory "obj/replay{n}/netcoreapp1.0" which we can use
            var repdir = "";
            for (int i = 0; ; i++)
            {
                repdir = $"{dir}/obj/Replay{(i == 0 ? "" : i.ToString())}/netcoreapp1.0";
                fn = $"{repdir}/{project.AssemblyName}.dll";
                if (File.Exists(fn)) try { File.Delete(fn); } catch (Exception) { }
                if (!File.Exists(fn)) break;
            }
            if (!File.Exists($"{repdir}/{project.AssemblyName}.deps.json"))
            {
                Directory.CreateDirectory(repdir);
                File.Copy($"{bindir}/{project.AssemblyName}.deps.json", $"{repdir}/{project.AssemblyName}.deps.json");
                File.Copy($"{bindir}/{project.AssemblyName}.runtimeconfig.dev.json", $"{repdir}/{project.AssemblyName}.runtimeconfig.dev.json");
                File.Copy($"{bindir}/{project.AssemblyName}.runtimeconfig.json", $"{repdir}/{project.AssemblyName}.runtimeconfig.json");
            }
        }

        var comp = await project.GetCompilationAsync(cancel).ConfigureAwait(false);
        var result = await Task.Run(() => comp.Emit(fn, cancellationToken: cancel)).ConfigureAwait(false);
        return new BuildResult(result, fn);
    }

    public static async Task<ReplayHostInstance> RunAsync(string outputFilePath, CancellationToken cancel = default(CancellationToken))
    {
        bool isCore = string.Compare(Path.GetExtension(outputFilePath), ".dll", true) == 0;

        Process process = null;
        try
        {
            process = new Process();
            process.StartInfo.FileName = isCore ? "dotnet" : outputFilePath;
            process.StartInfo.Arguments = isCore ? "exec \"" + outputFilePath + "\"" : null;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.CreateNoWindow = true;
            process.Start();
            var lineTask = process.StandardOutput.ReadLineAsync();
            var tcs = new TaskCompletionSource<object>();
            using (var reg = cancel.Register(tcs.SetCanceled)) await Task.WhenAny(lineTask, tcs.Task).ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();
            var line = await lineTask.ConfigureAwait(false);
            if (line != "OK") throw new Exception($"Expecting 'OK', got '{line}'");
            var host = new ReplayHostInstance { Process = process };
            process = null;
            return host;
        }
        finally
        {
            if (process != null && !process.HasExited) { process.Kill(); process.WaitForExit(); }
            if (process != null) process.Dispose(); process = null;
        }
    }

}

class TreeRewriter : CSharpSyntaxRewriter
{
    Compilation Compilation;
    SyntaxTree OriginalTree;
    SemanticModel SemanticModel;
    INamedTypeSymbol ConsoleType;

    public TreeRewriter(Compilation compilation, SyntaxTree tree)
    {
        Compilation = compilation;
        OriginalTree = tree;
        SemanticModel = compilation.GetSemanticModel(tree);
        ConsoleType = Compilation.GetTypeByMetadataName("System.Console");
    }

    public override SyntaxNode VisitBlock(BlockSyntax node)
        => node.WithStatements(SyntaxFactory.List(ReplaceStatements(node.Statements)));

    public override SyntaxNode VisitSwitchSection(SwitchSectionSyntax node)
        => node.WithStatements(SyntaxFactory.List(ReplaceStatements(node.Statements)));

    public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
        => node.Identifier.Text == "Replay" ? node : base.VisitClassDeclaration(node);

    public override SyntaxNode VisitCompilationUnit(CompilationUnitSyntax node)
    {
        var members = ReplaceMembers(node.Members);
        if (OriginalTree.Options.Kind == SourceCodeKind.Script)
        {
            var expr = SyntaxFactory_Log(null, null, null, null, -1);
            var member = SyntaxFactory.GlobalStatement(SyntaxFactory.ExpressionStatement(expr));
            members = new [] { member }.Concat(members);
        }
        return node.WithMembers(SyntaxFactory.List(members));
    }
        

    IEnumerable<MemberDeclarationSyntax> ReplaceMembers(IEnumerable<MemberDeclarationSyntax> members)
    {
        foreach (var member in members)
        {
            if (member.IsKind(SyntaxKind.GlobalStatement))
            {
                foreach (var statement in ReplaceStatements(new[] { (member as GlobalStatementSyntax).Statement }))
                {
                    yield return SyntaxFactory.GlobalStatement(statement);
                }
            }
            else if (member.IsKind(SyntaxKind.FieldDeclaration))
            {
                yield return Visit(member) as FieldDeclarationSyntax;
            }
            else
            {
                yield return Visit(member) as MemberDeclarationSyntax;
            }
        }
    }

    public override SyntaxNode VisitVariableDeclaration(VariableDeclarationSyntax declaration)
    {
        var type = SemanticModel.GetSymbolInfo(declaration.Type).Symbol as ITypeSymbol;
        if (type == null) return declaration;
        var variables = declaration.Variables;
        var locs = variables.Select(v => v.GetLocation()).ToArray();
        for (int i = 0; i < variables.Count; i++)
        {
            var oldVariable = variables[i];
            if (oldVariable.Initializer == null) continue;
            var id = oldVariable.Identifier.ValueText;
            var newValue = SyntaxFactory_Log(type, oldVariable.Initializer.Value, id, locs[i], 1);
            var newVariable = oldVariable.WithInitializer(oldVariable.Initializer.WithValue(newValue));
            variables = variables.Replace(oldVariable, newVariable);
        }
        return declaration.WithVariables(variables);
    }


    IEnumerable<StatementSyntax> ReplaceStatements(IEnumerable<StatementSyntax> statements)
    {
        // LocalDeclarationStatement  int x, y=10                  -> int x,y=Log<type>(10,"y",...,1);
        // ExpressionStatement        f();                         -> Log(f(),null,...,2) or f();Log(null,null,...,3);
        //    PropertyDeclaration     int p <accessors>
        //    PropertyDeclaration     int p <accessors> = e;       -> int p <accessors> = Log<type>(e,"p",...,12);
        //    PropertyDeclaration     int p => e;                  -> int p => Log<type>(e,"p",...,12);
        //    MethodDeclaration       int f() => e;                -> void f() => Log<type?>(e,"f",...,14);
        //    MethodDeclaration       void f() => e;               -> ??
        // EmptyStatement             ;
        // LabeledStatement           fred: <stmt>;
        // GotoStatement              goto fred;
        // SwitchStatement            switch (e) { <sections> }    -> switch(Log(e,"e"?,...,4)) { <sections> }
        // GotoCaseStatement          goto case 2;
        // GotoDefaultStatement       goto default;
        // BreakStatement             break;
        // ContinueStatement          continue;
        // ReturnStatement            return e;                    -> return; or return Log<type>(e,"e"?,...,5);
        // YieldReturnStatement       yield return e;              -> yield return Log<type>(e,"e"?,...,6);
        // YieldBreakStatement        yield break;
        // ThrowStatement             throw e;                     -> throw Log(e,"e"?,...,7);
        // WhileStatement             while (e) <stmt>             -> while (Log(e,"e"?,...,8)) { <stmts> }
        // DoStatement                do <stmt> while (e);         -> do { <stmts> } while (Log(e,"e"?,...,9);
        // ForStatement               for (<exprstmts | decl>; <exprs>; <exprstmts>) <stmt>  -> >>
        // ForEachStatement           foreach (var x in e) <stmt>  -> ??
        // UsingStatement             using (<decl | expr>) <stmt> -> ??
        // FixedStatement             fixed (<decl>) <stmt>        -> fixed (<decl>) { <stmts> }
        // CheckedStatement           checked <block>
        // UncheckedStatement         unchecked <block>
        // UnsafeStatement            unsafe <block>
        // LockStatement              lock (<expr>) <stmt>         -> lock (<expr>) { <stmts> }
        // IfStatement                if (<expr>) <stmt> <else>    -> ??
        // TryStatement               try <block> catch (<decl>) when e <block> finally <block> -> ??
        // GlobalStatement            ??
        // Block                      { <stmts> }                  -> { Log(null,null,...,10); <stmts> }
        //    MethodDeclaration       void f(int i) <block>        -> ??
        //    MethodDeclaration       void f(int i) <errors!>      -> ??
        //    ConstructorDeclaration  C(int i) <block>             -> ??
        //    AccessorDeclaration     set <block>                  -> set { Log(value,"value",...,13); <stmts> }
        // FieldDeclaration           int p = e;                   -> int p = Log<type>(e,"p",...,15);

        foreach (var statement0 in statements)
        {
            var statement1 = Visit(statement0) as StatementSyntax;

            //if (statement1.IsKind(SyntaxKind.LocalDeclarationStatement))
            //{
            //    // int x, y=10 -> int x,y=Log<type>(10,"y",...,1);
            //    var statement = statement1 as LocalDeclarationStatementSyntax;
            //    var declaration = VisitVariableDeclaration(statement.Declaration) as VariableDeclarationSyntax;
            //    yield return statement.WithDeclaration(declaration);
            //}
            //else
            if (statement1.IsKind(SyntaxKind.ExpressionStatement))
            {
                // f(); -> Log(f(),null,...,2) or f();Log(null,null,...,3);
                var statement = statement1 as ExpressionStatementSyntax;
                var expression = statement.Expression;
                var type = SemanticModel.GetTypeInfo(expression).ConvertedType;
                if (type == null) { yield return statement; continue; }
                bool isVoid = ((object)type == Compilation.GetSpecialType(SpecialType.System_Void));
                if (isVoid)
                {
                    yield return statement;
                    var log = SyntaxFactory_Log(null, null, null, statement.GetLocation(), 3);
                    yield return SyntaxFactory.ExpressionStatement(log);
                }
                else
                {
                    var log = SyntaxFactory_Log(type, expression, null, statement.GetLocation(), 2);
                    yield return statement.WithExpression(log);
                }
            }
            else
            {
                yield return statement1;
            }
        }
    }

    ExpressionSyntax SyntaxFactory_Log(ITypeSymbol type, ExpressionSyntax expr, string id, Location loc, int reason)
    {
        // Generates "global::System.Runtime.CompilerServices.Replay.Log<type>(expr,"id","loc.FilePath",loc.Line,reason)"
        // type, expr and id may be null.

        if (type == null && expr == null) type = Compilation.GetSpecialType(SpecialType.System_Object);
        if (expr == null) expr = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
        string file = (loc == null) ? null : loc.SourceTree.FilePath;
        int line = (loc == null) ? -1 : loc.GetMappedLineSpan().StartLinePosition.Line;

        var typeName = SyntaxFactory.ParseTypeName(type.ToDisplayString());
        var replay = SyntaxFactory.QualifiedName(
                SyntaxFactory.QualifiedName(
                    SyntaxFactory.QualifiedName(
                        SyntaxFactory.AliasQualifiedName(
                            SyntaxFactory.IdentifierName(SyntaxFactory.Token(SyntaxKind.GlobalKeyword)),
                            SyntaxFactory.IdentifierName("System")),
                        SyntaxFactory.IdentifierName("Runtime")),
                    SyntaxFactory.IdentifierName("CompilerServices")),
                SyntaxFactory.IdentifierName("Replay"));
        var log = (type == null)
            ? SyntaxFactory.QualifiedName(replay, SyntaxFactory.IdentifierName("Log"))
            : SyntaxFactory.QualifiedName(replay, SyntaxFactory.GenericName(SyntaxFactory.Identifier("Log"),
                    SyntaxFactory.TypeArgumentList(SyntaxFactory.SeparatedList(new[] { typeName }))));
        var args = SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] {
                        SyntaxFactory.Argument(expr),
                        SyntaxFactory.Argument(id == null ? SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression) : SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(id))),
                        SyntaxFactory.Argument(file == null ? SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression) : SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(file))),
                        SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(line))),
                        SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(reason)))
                    }));
        var invocation = SyntaxFactory.InvocationExpression(log, args);
        return invocation;
    }
}



class AsyncProcess : IDisposable
{
    private Process Process;

    public async Task PostLineAsync(string cmd, CancellationToken cancel = default(CancellationToken))
    {
        using (var reg = cancel.Register(Process.Kill)) await Process.StandardInput.WriteLineAsync(cmd);
    }

    public async Task<string> ReadLineAsync(CancellationToken cancel = default(CancellationToken))
    {
        using (var reg = cancel.Register(Process.Kill)) return await Process.StandardOutput.ReadLineAsync();
    }

    public void Dispose() => DisposeAsync().Wait();

    public async Task DisposeAsync()
    {
        if (Process?.HasExited == false) { Process.Kill(); await Task.Run(() => Process.WaitForExit()).ConfigureAwait(false); }
        if (Process != null) Process.Dispose(); Process = null;
    }
}
