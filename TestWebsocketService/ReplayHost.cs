using Microsoft.CodeAnalysis;
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
    private bool EditorHasOwnDatabase;
    private BufferBlock<Tuple<string, Project, TaskCompletionSource<object>>> Queue = new BufferBlock<Tuple<string, Project, TaskCompletionSource<object>>>();
    private int TagCounter;
    private Task RunTask;
    private CancellationTokenSource RunCancel = new CancellationTokenSource();

    public delegate void AdornmentChangedHandler(bool isAdd, int tag, int line, string content, TaskCompletionSource<object> deferral, CancellationToken cancel);
    public delegate void DiagnosticChangedHandler(bool isAdd, int tag, Diagnostic diagnostic, TaskCompletionSource<object> deferral, CancellationToken cancel);
    public delegate void ReplayHostError(string error, TaskCompletionSource<object> deferral, CancellationToken cancel);
    public event AdornmentChangedHandler AdornmentChanged;
    public event DiagnosticChangedHandler DiagnosticChanged;
    public event ReplayHostError Erred;

    public ReplayHost(bool editorHasOwnDatabase)
    {
        EditorHasOwnDatabase = editorHasOwnDatabase;
        RunTask = RunAsync(RunCancel.Token);
    }

    public async Task DisposeAsync()
    {
        try
        {
            var t = Interlocked.Exchange(ref RunTask, null);
            if (t == null) return;
            RunCancel.Cancel();
            await t.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        DisposeAsync().GetAwaiter().GetResult();
    }

    public Task DocumentHasChangedAsync(Project project, string file, int line, int count, int newcount)
    {
        if (project == null) throw new ArgumentNullException(nameof(project));
        var tcs = new TaskCompletionSource<object>();
        Queue.Post(Tuple.Create($"CHANGE\t{file}\t{line}\t{count}\t{newcount}", project, tcs));
        return tcs.Task;
    }
    public Task ViewHasChangedAsync(string file, int line, int count)
    {
        if (file == null) throw new ArgumentNullException(nameof(file));
        var tcs = new TaskCompletionSource<object>();
        Queue.Post(Tuple.Create($"WATCH\t{file}\t{line}\t{count}", (Project)null, tcs));
        return tcs.Task;
    }

    private Task SendAdornmentChangeAsync(bool isAdd, int tag, int line, string content, CancellationToken cancel)
    {
        var c = AdornmentChanged;
        if (c == null) return Task.FromResult(0);
        var tcs = new TaskCompletionSource<object>();
        c.Invoke(isAdd, tag, line, content, tcs, cancel);
        return tcs.Task;
    }

    private Task SendDiagnosticChangeAsync(bool isAdd, int tag, Diagnostic diagnostic, CancellationToken cancel)
    {
        var c = DiagnosticChanged;
        if (c == null) return Task.FromResult(0);
        var tcs = new TaskCompletionSource<object>();
        c.Invoke(isAdd, tag, diagnostic, tcs, cancel);
        return tcs.Task;
    }

    private Task SendErrorAsync(string error, CancellationToken cancel)
    {
        var c = Erred;
        if (c == null) return Task.FromResult(0);
        var tcs = new TaskCompletionSource<object>();
        c.Invoke(error, tcs, cancel);
        return tcs.Task;
    }


    struct TaggedAdornment
    {
        public readonly string File;
        public readonly int Line;
        public readonly string Content;
        public readonly int ContentHash;
        public readonly int Tag;

        public TaggedAdornment(string file, int line, string content, int hash, int tag)
        {
            File = file; Line = line; Content = content; ContentHash = hash; Tag = tag;
        }
        public TaggedAdornment WithLine(int line) => new TaggedAdornment(File, line, Content, ContentHash, Tag);
        public override string ToString() => $"{Path.GetFileName(File)}({Line}):{Content}  [#{Tag}]";
    }

    struct TaggedDiagnostic
    {
        public readonly Diagnostic Diagnostic;
        public readonly int Tag;

        public TaggedDiagnostic(int tag, Diagnostic diagnostic)
        {
            Tag = tag; Diagnostic = diagnostic;
        }
    }

    private async Task RunAsync(CancellationToken cancel)
    {
        // This is the state of the client
        var Database1 = new List<TaggedDiagnostic>();
        var Database2 = new Dictionary<string, Dictionary<int, TaggedAdornment>>();
        string watchFile = null;
        int watchLine = -1, watchCount = -1;
        var getProcessCancel = null as CancellationTokenSource;
        var getProcessTask = null as Task<AsyncProcess>;
        var runProcessTcs = null as TaskCompletionSource<object>;
        var watchTcs = new LinkedList<Tuple<string,TaskCompletionSource<object>>>();


        var queueTask = Queue.ReceiveAsync();
        var readProcessTask = null as Task<string>;

        try
        {

            while (true)
            {
                var tasks = new List<Task>(4);
                tasks.Add(queueTask);
                if (readProcessTask == null && getProcessTask != null) tasks.Add(getProcessTask);
                if (readProcessTask != null) tasks.Add(readProcessTask);
                var tcsCancel = new TaskCompletionSource<object>(); tasks.Add(tcsCancel.Task);
                using (var reg = cancel.Register(tcsCancel.SetCanceled)) await Task.WhenAny(tasks).ConfigureAwait(false);

                cancel.ThrowIfCancellationRequested();


                if (getProcessTask?.Status == TaskStatus.RanToCompletion && readProcessTask == null && getProcessTask.Result != null)
                {
                    // This is the first time we hear that the new process is up
                    readProcessTask = getProcessTask.Result.ReadLineAsync(cancel);

                    if (watchFile != null)
                    {
                        var hashes = (watchFile != null && Database2.ContainsKey(watchFile))
                                     ? Database2[watchFile].Values.Where((ta) => watchLine <= ta.Line && ta.Line < watchLine + watchCount).ToList()
                                     : new List<TaggedAdornment>();
                        // WATCH correlation file line count nhashes line0 hash0 ... lineN hashN
                        var process = getProcessTask.Result;
                        var msg = string.Join("\t", hashes.Select(ta => $"{ta.Line}\t{ta.ContentHash}"));
                        msg = $"WATCH\t\t{watchFile}\t{watchLine}\t{watchCount}\t{hashes.Count}\t{msg}".TrimEnd(new[] { '\t' });
                        await process.PostLineAsync(msg, cancel).ConfigureAwait(false);
                    }
                    continue;
                }

                if (getProcessTask?.IsFaulted == true)
                {
                    string msg = "error"; try { getProcessTask.GetAwaiter().GetResult(); } catch (Exception ex) { msg = ex.Message; }
                    getProcessTask = null;
                    await SendErrorAsync($"ERROR\tBuild failed: '{msg}'", cancel).ConfigureAwait(false);
                    continue;
                }

                if (queueTask?.Status == TaskStatus.RanToCompletion && queueTask.Result.Item1.StartsWith("CHANGE\t"))
                {
                    var cmd = queueTask.Result; queueTask = Queue.ReceiveAsync();
                    var cmdproject = cmd.Item2;
                    runProcessTcs?.TrySetCanceled(); runProcessTcs = cmd.Item3;
                    foreach (var tcs in watchTcs) tcs.Item2.TrySetCanceled();
                    watchTcs.Clear();
                    var cmds = cmd.Item1.Split(new[] { '\t' });
                    string file = cmds[1];
                    int line = int.Parse(cmds[2]), count = int.Parse(cmds[3]), newcount = int.Parse(cmds[4]);

                    // Modify entries in the database: delete all line <= entry < line+count; add (newcount-count) to all line+count <= entry
                    if (file != null && Database2.ContainsKey(file))
                    {
                        var dbfile = new Dictionary<int, TaggedAdornment>();
                        foreach (var entry in Database2[file])
                        {
                            if (entry.Key < line) dbfile[entry.Key] = entry.Value;
                            else if (line <= entry.Key && entry.Key < line + count) { }
                            else dbfile[entry.Key + newcount - count] = entry.Value.WithLine(entry.Key + newcount - count);
                        }
                        Database2[file] = dbfile;
                    }

                    // Rebuild + restart the process
                    getProcessCancel?.Cancel();
                    getProcessCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    getProcessTask = GetProcessAsync(cmdproject, Database1, getProcessTask, runProcessTcs, getProcessCancel.Token);
                    readProcessTask = null;
                    continue;
                }

                if (queueTask?.Status == TaskStatus.RanToCompletion && queueTask.Result.Item1.StartsWith("WATCH\t"))
                {
                    var cmd = queueTask.Result; queueTask = Queue.ReceiveAsync();
                    var cmds = cmd.Item1.Split(new[] { '\t' });
                    string file = cmds[1];
                    int line = int.Parse(cmds[2]), count = int.Parse(cmds[3]);
                    var tcs = cmd.Item3;
                    //
                    var hashes = (file != null && Database2.ContainsKey(file))
                                 ? Database2[file].Values.Where((ta) => line <= ta.Line && ta.Line < line + count).ToList()
                                 : new List<TaggedAdornment>();
                    if (!EditorHasOwnDatabase)
                    {
                        foreach (var ta in hashes) await SendAdornmentChangeAsync(true, ta.Tag, ta.Line, ta.Content, cancel).ConfigureAwait(false);
                    }
                    //
                    if (watchFile == file && watchLine == line && watchCount == count) { tcs.TrySetResult(null); continue; }
                    watchFile = file; watchLine = line; watchCount = count;
                    if (getProcessTask?.Status != TaskStatus.RanToCompletion) { tcs.TrySetResult(null); continue; }

                    // WATCH correlation file line count nhashes line0 hash0 ... lineN hashN
                    var correlation = ++TagCounter;
                    watchTcs.AddLast(Tuple.Create(correlation.ToString(), tcs));
                    var process = getProcessTask.Result;
                    var msg = string.Join("\t", hashes.Select(ta => $"{ta.Line}\t{ta.ContentHash}"));
                    msg = $"WATCH\t{correlation}\t{watchFile}\t{watchLine}\t{watchCount}\t{hashes.Count}\t{msg}".TrimEnd(new[] { '\t' });
                    await process.PostLineAsync(msg, cancel).ConfigureAwait(false);
                    continue;
                }

                if (queueTask?.IsCompleted == true)
                {
                    string msg; try { msg = queueTask.GetAwaiter().GetResult().Item1; } catch (Exception ex) { msg = ex.Message; }
                    queueTask = Queue.ReceiveAsync();
                    await SendErrorAsync($"ERROR\tHost expected CHANGE|WATCH, got '{msg}'", cancel).ConfigureAwait(false);
                    continue;
                }

                if (readProcessTask?.Status == TaskStatus.RanToCompletion && readProcessTask.Result.StartsWith("REPLAY\t"))
                {
                    var cmd = readProcessTask.Result; readProcessTask = getProcessTask.Result.ReadLineAsync(cancel);
                    var cmds = cmd.Split(new[] { '\t' });
                    // REPLAY add line hash content
                    // REPLAY remove line
                    int line = -1, hash = -1; string content = null;
                    bool ok = false;
                    if (cmds.Length == 5 && cmds[1] == "add" && int.TryParse(cmds[2], out line) && int.TryParse(cmds[3], out hash) && (content = cmds[4]) != null) ok = true;
                    if (cmds.Length == 3 && cmds[1] == "remove" && int.TryParse(cmds[2], out line)) ok = true;
                    if (!ok) { await SendErrorAsync($"ERROR\tHost expected 'REPLAY add line hash content | REPLAY remove line', got '{cmd}'", cancel); continue; }
                    //
                    if (cmds[1] == "remove")
                    {
                        Dictionary<int, TaggedAdornment> dbfile; ok = Database2.TryGetValue(watchFile, out dbfile);
                        if (ok)
                        {
                            TaggedAdornment ta; ok = dbfile.TryGetValue(line, out ta);
                            if (ok)
                            {
                                await SendAdornmentChangeAsync(false, ta.Tag, -1, null, cancel).ConfigureAwait(false);
                                dbfile.Remove(line);
                                ok = (hash == ta.ContentHash);
                            }
                        }
                        if (!ok) await SendErrorAsync($"ERROR\tHost database lacks '{cmd}'", cancel).ConfigureAwait(false);
                    }
                    else if (cmds[1] == "add")
                    {
                        if (watchFile == null) { await SendErrorAsync($"ERROR\tHost received 'REPLAY add' but isn't watching any files", cancel).ConfigureAwait(false); continue; }
                        if (!Database2.ContainsKey(watchFile)) Database2[watchFile] = new Dictionary<int, TaggedAdornment>();
                        var dbfile = Database2[watchFile];
                        TaggedAdornment ta; if (dbfile.TryGetValue(line, out ta))
                        {
                            await SendAdornmentChangeAsync(false, ta.Tag, -1, null, cancel).ConfigureAwait(false);
                        }
                        ta = new TaggedAdornment(watchFile, line, content, hash, ++TagCounter);
                        await SendAdornmentChangeAsync(true, ta.Tag, ta.Line, ta.Content, cancel).ConfigureAwait(false);
                        dbfile[line] = ta;
                    }
                    continue;
                }

                if (readProcessTask?.Status == TaskStatus.RanToCompletion && readProcessTask.Result.StartsWith("END\t"))
                {
                    var cmd = readProcessTask.Result; readProcessTask = getProcessTask.Result.ReadLineAsync(cancel);
                    var cmds = cmd.Split(new[] { '\t' });
                    if (cmds.Length < 2 || (cmds[1] != "run" && cmds[1] != "watch")
                        || (cmds[1] == "watch" && cmds.Length != 3))
                    {
                        await SendErrorAsync($"ERROR\tHost expected 'END run | END watch correlation', got '{cmd}'", cancel).ConfigureAwait(false); continue;
                    }
                    if (cmds[1] == "run")
                    {
                        runProcessTcs?.TrySetResult(null);
                    }
                    else if (cmds[1] == "watch")
                    {
                        string correlation = cmds[2];
                        if (watchTcs.Count == 0) await SendErrorAsync($"ERROR\tNot expecting '{cmd}'", cancel).ConfigureAwait(false);
                        else if (watchTcs.First.Value.Item1 != correlation) await SendErrorAsync($"ERROR\tExpecting 'END watch {watchTcs.First.Value.Item1}', got '{cmd}'",cancel).ConfigureAwait(false);
                        else { watchTcs.First.Value.Item2.TrySetResult(null); watchTcs.RemoveFirst(); }
                    }
                    continue;
                }

                if (readProcessTask?.Status == TaskStatus.RanToCompletion && readProcessTask.Result.StartsWith("ERROR\t"))
                {
                    var cmd = readProcessTask.Result; readProcessTask = getProcessTask.Result.ReadLineAsync(cancel);
                    await SendErrorAsync($"CLIENT{cmd}", cancel);
                    continue;
                }


                if (readProcessTask?.Status == TaskStatus.RanToCompletion && readProcessTask.Result.StartsWith("DEBUG\t"))
                {
                    var cmd = readProcessTask.Result; readProcessTask = getProcessTask.Result.ReadLineAsync(cancel);
                    await SendErrorAsync($"CLIENT{cmd}", cancel);
                    continue;
                }

                if (readProcessTask?.IsCompleted == true)
                {
                    string msg; try { msg = readProcessTask.GetAwaiter().GetResult(); } catch (Exception ex) { msg = ex.Message; }
                    readProcessTask = getProcessTask.Result.ReadLineAsync(cancel);
                    await SendErrorAsync($"ERROR\tHost expected REPLAY, got '{msg}'", cancel).ConfigureAwait(false);
                    continue;
                }

            }

        }
        catch (OperationCanceledException)
        {
            runProcessTcs?.TrySetCanceled();
            foreach (var tcs in watchTcs) tcs.Item2.TrySetCanceled();
            IList<Tuple<string, Project, TaskCompletionSource<object>>> items;
            if (Queue.TryReceiveAll(out items)) foreach (var item in items) item.Item3?.TrySetCanceled();
        }
        catch (Exception ex)
        {
            runProcessTcs?.TrySetException(ex);
            foreach (var tcs in watchTcs) tcs.Item2.TrySetException(ex);
            IList<Tuple<string, Project, TaskCompletionSource<object>>> items;
            if (Queue.TryReceiveAll(out items)) foreach (var item in items) item.Item3?.TrySetException(ex);
            throw;
        }
        finally
        {
            runProcessTcs?.TrySetResult(null);
            foreach (var tcs in watchTcs) tcs.Item2.TrySetResult(null);
            IList<Tuple<string, Project, TaskCompletionSource<object>>> items;
            if (Queue.TryReceiveAll(out items)) foreach (var item in items) item.Item3?.TrySetResult(null);
        }
    }

    async Task DumpAsync(string msg, Dictionary<string, Dictionary<int, TaggedAdornment>> Database2, string fn, CancellationToken cancel)
    {
        await SendErrorAsync($"DEBUG\t{msg}", cancel).ConfigureAwait(false);
        if (fn == null || !Database2.ContainsKey(fn)) { await SendErrorAsync("DEBUG\t<no files>",cancel).ConfigureAwait(false); return; }
        var dbfile = Database2[fn];
        foreach (var kv in dbfile)
        {
            await SendErrorAsync($"DEBUG\t({kv.Key}): {kv.Value} hash={kv.Value.ContentHash}", cancel).ConfigureAwait(false); ;
        }
    }

    async Task<AsyncProcess> GetProcessAsync(Project project, List<TaggedDiagnostic> database, Task<AsyncProcess> prevTask, TaskCompletionSource<object> runProcessTcs, CancellationToken cancel)
    {
        AsyncProcess prevProcess = null;
        if (prevTask != null) try { prevProcess = await prevTask.ConfigureAwait(false); } catch (Exception) { }
        if (prevProcess != null) await prevProcess.DisposeAsync().ConfigureAwait(false);

        if (project == null)
        {
            foreach (var td in database) await SendDiagnosticChangeAsync(false, td.Tag, td.Diagnostic, cancel).ConfigureAwait(false);
            database.Clear();
            return null;
        }

        var originalComp = await project.GetCompilationAsync(cancel).ConfigureAwait(false);
        var originalDiagnostics = originalComp.GetDiagnostics(cancel);
        bool success = false; string outputFilePath = null;
        if (!originalDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            project = await InstrumentProjectAsync(project, cancel).ConfigureAwait(false);
            var results = await BuildAsync(project, cancel).ConfigureAwait(false);
            success = results.Success; outputFilePath = results.ReplayOutputFilePath;
            var annotatedDiagnostics = results.Diagnostics;
            var causedByAnnotation = annotatedDiagnostics.Except(originalDiagnostics, DiagnosticUserFacingComparer.Default);
            foreach (var diagnostic in causedByAnnotation) await SendErrorAsync($"ERROR\tInstrumenting error: '{DiagnosticUserFacingComparer.ToString(diagnostic)}'", cancel).ConfigureAwait(false);
        }

        // Remove+add diagnostics as needed
        foreach (var td in database.ToArray())
        {
            if (originalDiagnostics.Contains(td.Diagnostic, DiagnosticUserFacingComparer.Default)) continue;
            await SendDiagnosticChangeAsync(false, td.Tag, td.Diagnostic, cancel).ConfigureAwait(false);
            database.Remove(td);
        }
        foreach (var d in originalDiagnostics)
        {
            if (database.Any(td => DiagnosticUserFacingComparer.Default.Equals(td.Diagnostic, d))) continue;
            var tag = ++TagCounter;
            await SendDiagnosticChangeAsync(true, tag, d, cancel);
            database.Add(new TaggedDiagnostic(tag,d));
        }

        if (!success)
        {
            runProcessTcs?.TrySetResult(null);
            return null;
        }

        // Launch the process
        var process = await LaunchProcessAsync(outputFilePath, cancel).ConfigureAwait(false);
        return process;
    }



    public static async Task<Project> InstrumentProjectAsync(Project originalProject, CancellationToken cancel)
    {
        var project = originalProject;

        var originalComp = await originalProject.GetCompilationAsync(cancel).ConfigureAwait(false);
        var replay = originalComp.GetTypeByMetadataName("System.Runtime.CompilerServices.Replay");
        if (replay == null)
        {
            var fn = typeof(ReplayHost).GetTypeInfo().Assembly.Location;
            for (; fn != null; fn = Path.GetDirectoryName(fn))
            {
                if (File.Exists(fn + "/ReplayClient.cs")) break;
            }
            if (fn == null) fn = @"C:\Users\lwischik\source\Repos\replay";
            if (fn == null) throw new Exception("class 'Replay' not found");
            fn = fn + "/ReplayClient.cs";
            var document = project.AddDocument("ReplayClient.cs", File.ReadAllText(fn), null, fn);
            project = document.Project;
        }

        foreach (var documentId in originalProject.DocumentIds)
        {
            var document = await InstrumentDocumentAsync(project.GetDocument(documentId), cancel).ConfigureAwait(false);
            project = document.Project;
        }
        return project;
    }

    public static async Task<Document> InstrumentDocumentAsync(Document document, CancellationToken cancel)
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

    public static async Task<BuildResult> BuildAsync(Project project, CancellationToken cancel)
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
            if (!File.Exists($"{bindir}/{project.AssemblyName}.deps.json"))
            {
                using (var process = new Process())
                {
                    process.StartInfo.FileName = "dotnet.exe";
                    process.StartInfo.Arguments = "build";
                    process.StartInfo.WorkingDirectory = dir;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();
                    var opTask = process.StandardOutput.ReadToEndAsync();
                    var errTask = process.StandardError.ReadToEndAsync();
                    using (var reg = cancel.Register(process.Kill)) await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
                    cancel.ThrowIfCancellationRequested();
                    var op = await opTask.ConfigureAwait(false);
                    var err = await errTask.ConfigureAwait(false);
                    if (!File.Exists($"{bindir}/{project.AssemblyName}.deps.json")) throw new Exception($"Failed to dotnet build - {err}s{op}");
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

    public static async Task<AsyncProcess> LaunchProcessAsync(string outputFilePath, CancellationToken cancel)
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
            var host = new AsyncProcess(process);
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


class DiagnosticUserFacingComparer : IEqualityComparer<Diagnostic>
{
    public static DiagnosticUserFacingComparer Default = new DiagnosticUserFacingComparer();

    public static string ToString(Diagnostic diagnostic)
    {
        var file = "";
        int offset = -1, length = -1;
        if (diagnostic.Location.IsInSource) { file = diagnostic.Location.SourceTree.FilePath; offset = diagnostic.Location.SourceSpan.Start; length = diagnostic.Location.SourceSpan.Length; }
        var dmsg = $"{diagnostic.Id}: {diagnostic.GetMessage()}";
        return $"{diagnostic.Severity}\t{file}\t{offset}\t{length}\t{dmsg}";
    }
    public bool Equals(Diagnostic x, Diagnostic y) => ToString(x) == ToString(y);
    public int GetHashCode(Diagnostic x) => ToString(x).GetHashCode();
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

    public AsyncProcess(Process process)
    {
        Process = process;
    }

    public async Task PostLineAsync(string cmd, CancellationToken cancel)
    {
        using (var reg = cancel.Register(Process.Kill)) await Process.StandardInput.WriteLineAsync(cmd).ConfigureAwait(false);
    }

    public async Task<string> ReadLineAsync(CancellationToken cancel)
    {
        using (var reg = cancel.Register(Process.Kill)) return await Process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        DisposeAsync().GetAwaiter().GetResult();
    }

    public async Task DisposeAsync()
    {
        try
        {
            var p = Interlocked.Exchange(ref Process, null);
            if (p == null) return;
            if (!p.HasExited) { p.Kill(); await Task.Run(() => p.WaitForExit()).ConfigureAwait(false); }
            p.Dispose();
        }
        catch (OperationCanceledException)
        {
        }
    }
}
