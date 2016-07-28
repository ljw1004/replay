using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

// TODO: use strongly typed Command virtual class for queue
// TODO: do deferrals properly


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

interface IDeferrable { IDeferral GetDeferral(); }
interface IDeferral { void Complete(); }

class Deferrable : IDeferrable
{
    private TaskCompletionSource<object> tcs;
    public IDeferral GetDeferral() => new Deferral(tcs = new TaskCompletionSource<object>());
    public System.Runtime.CompilerServices.TaskAwaiter GetAwaiter() => (tcs == null) ? Task.CompletedTask.GetAwaiter() : (tcs.Task as Task).GetAwaiter();
}

class Deferral : IDeferral
{
    private TaskCompletionSource<object> tcs;
    public Deferral(TaskCompletionSource<object> tcs) { this.tcs = tcs; }
    public void Complete() { tcs.SetResult(null); }
}


class ReplayHost : IDisposable
{
    private bool EditorHasOwnDatabase;
    private BufferBlock<Command> Queue = new BufferBlock<Command>();
    private int TagCounter;
    private Task RunTask;
    private CancellationTokenSource RunCancel = new CancellationTokenSource();

    public delegate void AdornmentChangedHandler(bool isAdd, int tag, string file, int line, string content, IDeferrable deferrable, CancellationToken cancel);
    public delegate void DiagnosticChangedHandler(bool isAdd, int tag, Diagnostic diagnostic, IDeferrable deferrable, CancellationToken cancel);
    public delegate void ReplayHostError(string error, IDeferrable deferrable, CancellationToken cancel);
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

    public Task ChangeDocumentAsync(Project project, string file, int line, int count, int newcount)
    {
        if (project == null) throw new ArgumentNullException(nameof(project));
        var tcs = new TaskCompletionSource<object>();
        var cmd = new ChangeDocumentCommand { Project = project, File = file, Line = line, Count = count, NewCount = newcount, Tcs=tcs };
        Queue.Post(cmd);
        return tcs.Task;
    }
    public Task WatchAsync(string file="*", int line=-1, int count=-1)
    {
        if (file == null) throw new ArgumentNullException(nameof(file));
        var tcs = new TaskCompletionSource<object>();
        var cmd = new WatchCommand { File = file, Line = line, Count = count, Tcs = tcs };
        Queue.Post(cmd);
        return tcs.Task;
    }

    abstract class Command
    {
        public TaskCompletionSource<object> Tcs;
    }
    class ChangeDocumentCommand : Command
    {
        public Project Project;
        public string File;
        public int Line;
        public int Count;
        public int NewCount;
    }
    class WatchCommand : Command
    {
        public string File;
        public int Line;
        public int Count;
    }

    private async Task SendAdornmentChangeAsync(bool isAdd, int tag, string file, int line, string content, CancellationToken cancel)
    {
        var deferrable = new Deferrable();
        AdornmentChanged?.Invoke(isAdd, tag, file, line, content, deferrable, cancel);
        await deferrable;
    }

    private async Task SendDiagnosticChangeAsync(bool isAdd, int tag, Diagnostic diagnostic, CancellationToken cancel)
    {
        var deferrable = new Deferrable();
        DiagnosticChanged?.Invoke(isAdd, tag, diagnostic, deferrable, cancel);
        await deferrable;
    }

    private async Task SendErrorAsync(string error, CancellationToken cancel)
    {
        var deferrable = new Deferrable();
        Erred.Invoke(error, deferrable, cancel);
        await deferrable;
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


                if (getProcessTask?.Status == TaskStatus.RanToCompletion && readProcessTask == null)
                {
                    // This is the first time we hear that the build+launch finished.
                    if (getProcessTask.Result == null)
                    {
                        // The build finished and has produced its diagnostics...
                        getProcessTask = null;
                    }
                    else
                    {
                        // The build finished, the process launched, and we're in dialog with it
                        var process = getProcessTask.Result;
                        readProcessTask = process.ReadLineAsync(cancel);
                        var msg = MakeWatchCommand(null, watchFile, watchLine, watchCount, Database2);
                        if (msg != null) await process.PostLineAsync(msg, cancel).ConfigureAwait(false);
                        continue;
                    }
                }

                if (getProcessTask?.IsFaulted == true)
                {
                    string msg = "error"; try { getProcessTask.GetAwaiter().GetResult(); } catch (Exception ex) { msg = ex.Message; }
                    getProcessTask = null;
                    await SendErrorAsync($"ERROR\tBuild failed: '{msg}'", cancel).ConfigureAwait(false);
                    continue;
                }

                if (queueTask?.Status == TaskStatus.RanToCompletion && queueTask.Result is ChangeDocumentCommand)
                {
                    var cmd = queueTask.Result as ChangeDocumentCommand; queueTask = Queue.ReceiveAsync();
                    var cmdproject = cmd.Project;
                    runProcessTcs?.TrySetResult(null); runProcessTcs = cmd.Tcs;
                    foreach (var tcs in watchTcs) tcs.Item2.TrySetCanceled();
                    watchTcs.Clear();
                    string file = cmd.File;
                    int line = cmd.Line, count = cmd.Count, newcount = cmd.NewCount;

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

                if (queueTask?.Status == TaskStatus.RanToCompletion && queueTask.Result is WatchCommand)
                {
                    var cmd = queueTask.Result as WatchCommand; queueTask = Queue.ReceiveAsync();
                    string file = cmd.File;
                    int line = cmd.Line, count = cmd.Count;
                    var tcs = cmd.Tcs;
                    //
                    var hashes = new Dictionary<string,List<TaggedAdornment>>();
                    foreach (var dbkv in Database2)
                    {
                        if (file != "*" && file != dbkv.Key) continue;
                        hashes[dbkv.Key] = new List<TaggedAdornment>();
                        foreach (var kv in dbkv.Value)
                        {
                            var ta = kv.Value;
                            if (line == -1 && count == -1) { }
                            else if (line <= ta.Line && ta.Line < line + count) { }
                            else continue;
                            hashes[dbkv.Key].Add(ta);
                        }
                    }
                    if (!EditorHasOwnDatabase)
                    {
                        foreach (var dbkv in hashes) foreach (var ta in dbkv.Value) await SendAdornmentChangeAsync(true, ta.Tag, ta.File, ta.Line, ta.Content, cancel).ConfigureAwait(false);
                    }
                    //
                    if (watchFile == file && watchLine == line && watchCount == count) { tcs.TrySetResult(null); continue; }
                    watchFile = file; watchLine = line; watchCount = count;
                    if (getProcessTask?.Status != TaskStatus.RanToCompletion || getProcessTask.Result == null) { tcs.TrySetResult(null); continue; }

                    // WATCH correlation file line count hashes...
                    var correlation = ++TagCounter;
                    watchTcs.AddLast(Tuple.Create(correlation.ToString(), tcs));
                    var process = getProcessTask.Result;
                    var msg = MakeWatchCommand(correlation, watchFile, watchLine, watchCount, Database2);
                    if (msg != null) await process.PostLineAsync(msg, cancel).ConfigureAwait(false);
                    continue;
                }

                if (queueTask?.IsCompleted == true)
                {
                    string msg; try { msg = queueTask.GetAwaiter().GetResult().ToString(); } catch (Exception ex) { msg = ex.Message; }
                    queueTask = Queue.ReceiveAsync();
                    await SendErrorAsync($"ERROR\tHost expected CHANGE|WATCH, got '{msg}'", cancel).ConfigureAwait(false);
                    continue;
                }

                if (readProcessTask?.Status == TaskStatus.RanToCompletion && readProcessTask.Result.StartsWith("REPLAY\t"))
                {
                    var cmd = readProcessTask.Result; readProcessTask = getProcessTask.Result.ReadLineAsync(cancel);
                    var cmds = cmd.Split(new[] { '\t' });
                    // REPLAY add file line hash content
                    // REPLAY remove file line hash
                    int line = -1, hash = -1; string file = null, content = null;
                    bool ok = false;
                    if (cmds.Length == 6 && cmds[1] == "add" && (file = cmds[2]) != null && int.TryParse(cmds[3], out line) && int.TryParse(cmds[4], out hash) && (content = cmds[5]) != null) ok = true;
                    if (cmds.Length == 5 && cmds[1] == "remove" && (file = cmds[2]) != null && int.TryParse(cmds[3], out line) && int.TryParse(cmds[4], out hash)) ok = true;
                    if (!ok) { await SendErrorAsync($"ERROR\tHost expected 'REPLAY add file line hash content | REPLAY remove file line hash', got '{cmd}'", cancel); continue; }
                    //
                    if (cmds[1] == "remove")
                    {
                        Dictionary<int, TaggedAdornment> dbfile; ok = Database2.TryGetValue(file, out dbfile);
                        if (ok)
                        {
                            TaggedAdornment ta; ok = dbfile.TryGetValue(line, out ta);
                            if (ok)
                            {
                                await SendAdornmentChangeAsync(false, ta.Tag, ta.File, -1, null, cancel).ConfigureAwait(false);
                                dbfile.Remove(line);
                                ok = (hash == ta.ContentHash);
                            }
                        }
                        if (!ok) await SendErrorAsync($"ERROR\tHost database lacks '{cmd}'", cancel).ConfigureAwait(false);
                    }
                    else if (cmds[1] == "add")
                    {
                        if (watchFile == null) { await SendErrorAsync($"ERROR\tHost received 'REPLAY add' but isn't watching any files", cancel).ConfigureAwait(false); continue; }
                        if (!Database2.ContainsKey(file)) Database2[file] = new Dictionary<int, TaggedAdornment>();
                        var dbfile = Database2[file];
                        TaggedAdornment ta; if (dbfile.TryGetValue(line, out ta))
                        {
                            await SendAdornmentChangeAsync(false, ta.Tag, ta.File, -1, null, cancel).ConfigureAwait(false);
                        }
                        ta = new TaggedAdornment(file, line, content, hash, ++TagCounter);
                        await SendAdornmentChangeAsync(true, ta.Tag, ta.File, ta.Line, ta.Content, cancel).ConfigureAwait(false);
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
            IList<Command> items;
            if (Queue.TryReceiveAll(out items)) foreach (var item in items) item.Tcs?.TrySetCanceled();
        }
        catch (Exception ex)
        {
            runProcessTcs?.TrySetException(ex);
            foreach (var tcs in watchTcs) tcs.Item2.TrySetException(ex);
            IList<Command> items;
            if (Queue.TryReceiveAll(out items)) foreach (var item in items) item.Tcs?.TrySetException(ex);
            throw;
        }
        finally
        {
            runProcessTcs?.TrySetResult(null);
            foreach (var tcs in watchTcs) tcs.Item2.TrySetResult(null);
            IList<Command> items;
            if (Queue.TryReceiveAll(out items)) foreach (var item in items) item.Tcs?.TrySetResult(null);
        }
    }

    static string MakeWatchCommand(int? correlation, string watchFile, int watchLine, int watchCount, Dictionary<string, Dictionary<int, TaggedAdornment>> Database2)
    {
        // WATCH correlation watchFile watchLine watchCount hashFileA hashLineA1 hashValA1 hashLineA2 hashValA2 hashFileB ...
        if (watchFile == null) return null;
        var sb = new StringBuilder();
        sb.Append($"WATCH\t{correlation}\t{watchFile}\t{watchLine}\t{watchCount}");
        foreach (var dbkv in Database2)
        {
            if (watchFile != "*" && watchFile != dbkv.Key) continue;
            string toAppendFile = dbkv.Key;
            foreach (var kv in dbkv.Value)
            {
                if (watchLine == -1 && watchCount == -1) { }
                else if (watchLine <= kv.Key && kv.Key < watchLine + watchCount) { }
                else continue;
                if (toAppendFile != null) { sb.Append("\t"); sb.Append(toAppendFile); toAppendFile = null; }
                sb.Append($"\t{kv.Key}\t{kv.Value.ContentHash}");
            }
        }
        return sb.ToString();
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

        foreach (var documentId in project.DocumentIds) if (project.GetDocument(documentId).Name.EndsWith(".md")) project = project.RemoveDocument(documentId);
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
            if (fn == null) throw new Exception("class 'Replay' not found");
            fn = fn + "/ReplayClient.cs";
            var document = project.AddDocument("ReplayClient.cs", File.ReadAllText(fn), null, fn);
            project = document.Project;
        }

        var autoruns = new List<string>();
        foreach (var documentId in originalProject.DocumentIds)
        {
            var document = await InstrumentDocumentAsync(project.GetDocument(documentId), autoruns, cancel).ConfigureAwait(false);
            project = document.Project;
        }

        if (autoruns.Count > 0)
        {
            var acode = string.Join(",\r\n", autoruns.Select(s => "\"" + s.Replace("\t", "\\t") + "\""));
            acode = "string[] GetAutorunMethods() { return new[] {\r\n"
                    + acode +
                    "\r\n}; }\r\n";
            var document = project.AddDocument("GetAutorunMethods.csx", SourceText.From(acode)).WithSourceCodeKind(SourceCodeKind.Script);
            project = document.Project;
        }

        return project;
    }

    public static async Task<Document> InstrumentDocumentAsync(Document document, List<string> autoruns, CancellationToken cancel)
    {
        var oldComp = await document.Project.GetCompilationAsync(cancel).ConfigureAwait(false);
        var oldTree = await document.GetSyntaxTreeAsync(cancel).ConfigureAwait(false);
        var oldRoot = await oldTree.GetRootAsync(cancel).ConfigureAwait(false);
        var rewriter = new TreeRewriter(oldComp, oldTree, autoruns);
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

        if (false && project.OutputFilePath != null)
        {
            var dir = project.OutputFilePath.Replace("\\obj\\", "\\bin");
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
            // is .NETCore app. Trust that it came out of ScriptWorkspace
            for (int i = 0; ; i++)
            {
                fn = (i == 0) ? project.OutputFilePath : Path.ChangeExtension(project.OutputFilePath, $"_{i}.dll");
                if (File.Exists(fn)) try { File.Delete(fn); } catch (Exception) { }
                if (File.Exists(fn)) continue;
                if (!File.Exists(Path.ChangeExtension(fn, ".deps.json"))) File.Copy(Path.ChangeExtension(project.OutputFilePath, ".deps.json"), Path.ChangeExtension(fn, ".deps.json"));
                if (!File.Exists(Path.ChangeExtension(fn, ".runtimeconfig.dev.json"))) File.Copy(Path.ChangeExtension(project.OutputFilePath, ".runtimeconfig.dev.json"), Path.ChangeExtension(fn, ".runtimeconfig.dev.json"));
                if (!File.Exists(Path.ChangeExtension(fn, ".runtimeconfig.json"))) File.Copy(Path.ChangeExtension(project.OutputFilePath, ".runtimeconfig.json"), Path.ChangeExtension(fn, ".runtimeconfig.json"));
                break;
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
        return $"{diagnostic.Severity}\t{offset}\t{length}\t{dmsg}";
    }
    public static string ToFileName(Diagnostic diagnostic)
    {
        if (diagnostic.Location.IsInSource) return diagnostic.Location.SourceTree.FilePath;
        return "";
    }
    public static string ToFullString(Diagnostic diagnostic)
    {
        return ToFileName(diagnostic) + "\t" + ToString(diagnostic);
    }
    public bool Equals(Diagnostic x, Diagnostic y) => ToFullString(x) == ToFullString(y);
    public int GetHashCode(Diagnostic x) => ToFullString(x).GetHashCode();
}




class TreeRewriter : CSharpSyntaxRewriter
{
    Compilation Compilation;
    SyntaxTree OriginalTree;
    SemanticModel SemanticModel;
    INamedTypeSymbol ConsoleType;
    List<string> Autoruns;

    public TreeRewriter(Compilation compilation, SyntaxTree tree, List<string> autoruns)
    {
        Compilation = compilation;
        OriginalTree = tree;
        Autoruns = autoruns;
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
            else if (member.IsKind(SyntaxKind.MethodDeclaration))
            {
                var symbol = SemanticModel.GetDeclaredSymbol(member) as IMethodSymbol;
                var attrs = symbol?.GetAttributes() ?? ImmutableArray<AttributeData>.Empty;
                foreach (var attr in attrs)
                {
                    if (attr.AttributeClass.Name != "AutoRunAttribute") continue;
                    Location loc = member.GetLocation();
                    string file = (loc == null) ? null : loc.GetMappedLineSpan().HasMappedPath ? loc.GetMappedLineSpan().Path : loc.SourceTree.FilePath;
                    int line = (loc == null) ? -1 : loc.GetMappedLineSpan().StartLinePosition.Line;
                    var s = $"{symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}\t{symbol.Name}\t{file}\t{line}";
                    Autoruns?.Add(s);
                }
                yield return Visit(member) as MethodDeclarationSyntax;
            }
            else
            {
                yield return Visit(member) as MemberDeclarationSyntax;
            }
        }
    }

    public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        node = base.VisitMethodDeclaration(node) as MethodDeclarationSyntax;
        if (node.Body == null) return node;
        var log = SyntaxFactory_Log(null, null, null, null, 9); // TODO: fill this out properly
        // for now I've just put in a dummy to make sure the ReplayClient class has its static ctor executed
        node = node.WithBody(node.Body.WithStatements(node.Body.Statements.Insert(0, SyntaxFactory.ExpressionStatement(log))));
        return node;
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
        string file = (loc == null) ? null : loc.GetMappedLineSpan().HasMappedPath ? loc.GetMappedLineSpan().Path : loc.SourceTree.FilePath;
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




public static class ScriptWorkspace
{

    private static Regex reFence = new Regex("^( *)((?<back>````*)|(?<tilde>~~~~*)) *([^ \r\n]*)");
    private static Regex reNugetReference = new Regex(@"^\s*(#r)\s+""([^"",]+)(,\s*([^""]+))?""\s*(//.*)?$");


    public static async Task<Project> FromDirectoryScanAsync(string dir, CancellationToken cancel = default(CancellationToken))
    {
        Directory.CreateDirectory(dir + "/obj/replay");
        var projName = Path.GetFileName(dir);

        // Scrape all the #r nuget references out of the .csx/.md files
        var nugetReferences = new List<Tuple<string, string>>();
        foreach (var file in Directory.GetFiles(dir))
        {
            string csx;
            if (Path.GetExtension(file) == ".md") csx = Md2Csx(file, File.ReadAllText(file), false);
            else if (Path.GetExtension(file) == ".csx") csx = File.ReadAllText(file);
            else continue;
            foreach (var line in csx.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
            {
                // Look for lines of the form "#r NugetName [Version] [// comment]" (where NugetName doesn't end with .dll)
                // and keep NugetName + Version
                // Stop looking once we find a line that's not "//", not "#", and not whitespace.
                var s = line.Trim();
                if (!s.StartsWith("#") && !s.StartsWith("//") && s != "") break;
                var match = reNugetReference.Match(s);
                if (!match.Success) continue;
                var name = match.Groups[2].Value;
                var version = match.Groups[4].Value;
                nugetReferences.Add(Tuple.Create(name, version));
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
        foreach (var file in Directory.GetFiles(dir, "*.md"))
        {
            var txt = File.ReadAllText(file);
            var csx = Md2Csx(file, txt);
            project = project.AddDocument(Path.GetFileName(file), txt, null, Path.GetFileName(file)).Project;
            project = project.AddDocument(Path.GetFileName(file) + ".csx", csx, null, Path.GetFileName(file) + ".csx").WithSourceCodeKind(SourceCodeKind.Script).Project;
        }

        return project;
    }

    struct Span { public int startOffset, lengthWithoutEOL, lengthWithEOL; }

    public static string Md2Csx(string file, string src, bool commentOutNugetReferences = true)
    {

        // First, figure out the line-break positions
        var spans = new List<Span>();
        int i = 0, istart = 0;
        for (; i < src.Length;)
        {
            var c = src[i];
            if (c == '\r' && i + 1 < src.Length && src[i + 1] == '\n') { spans.Add(new Span { startOffset = istart, lengthWithoutEOL = i - istart, lengthWithEOL = i - istart + 2 }); i += 2; istart = i; }
            else if (c == '\r' || c == '\n') { spans.Add(new Span { startOffset = istart, lengthWithoutEOL = i - istart, lengthWithEOL = i - istart + 1 }); i += 1; istart = i; }
            else i++;
        }
        if (i != src.Length) spans.Add(new Span { startOffset = istart, lengthWithoutEOL = i - istart, lengthWithEOL = i - istart });

        // Now we can use that to parse the string
        var csx = new StringBuilder();
        //
        int? cbStart = null; string indent = "", fence = ""; var commentOutRefs = new List<int>();
        for (int iline = 0; iline < spans.Count; iline++)
        {
            var span = spans[iline];
            var line = src.Substring(span.startOffset, span.lengthWithoutEOL);
            if (cbStart == null) // not in a codeblock
            {
                var match = reFence.Match(line);
                if (!match.Success) continue;
                commentOutRefs.Clear();
                cbStart = span.startOffset + span.lengthWithEOL; // start of the first line after the fence
                indent = match.Groups[1].Value; fence = match.Groups[2].Value;
                csx.Append($"#line {iline+2} \"{Path.GetFileName(file)}\"{src.Substring(span.startOffset + span.lengthWithoutEOL, span.lengthWithEOL - span.lengthWithoutEOL)}");
                // +2 because (A) #line directives are 1-based, and (B) we need to specify the line number of the following line, not this one
            }
            else
            {
                string line2 = line, indent2 = indent; while (indent2.StartsWith(" ") && line2.StartsWith(" ")) { line2 = line2.Substring(1); indent2 = indent2.Substring(1); }
                if (line2.StartsWith(fence))
                {
                    // paste the code, but replacing every indicated "#r" with "//"
                    int ipos = cbStart.Value, iend = span.startOffset;
                    foreach (var commentOutRef in commentOutRefs)
                    {
                        csx.Append(src.Substring(ipos, commentOutRef - ipos));
                        csx.Append(commentOutNugetReferences ? "//" : "#r");
                        ipos = commentOutRef + 2;
                    }
                    csx.Append(src.Substring(ipos, iend - ipos));
                    // append a newline, using the same line terminator as the end fence
                    csx.Append(src.Substring(span.startOffset + span.lengthWithoutEOL, span.lengthWithEOL - span.lengthWithoutEOL));
                    cbStart = null;
                    continue;
                }
                var match = reNugetReference.Match(line);
                if (!match.Success) continue;
                commentOutRefs.Add(span.startOffset + match.Groups[1].Index);
            }
        }
        //
        return csx.ToString();
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
