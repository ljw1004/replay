using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace System.Runtime.CompilerServices
{
    public class Replay
    {
        // Client <-> Host <-> Editor
        // The host is always on the same machine as the replayer. They communicate by serialization.
        // The host and editor are in the same process (VSIX) or on remote machines (online).
        // The host can trigger the client to shut down and a new client to launch.
        
        // For now:
        // CLIENT: Each successive instance of the client builds up a database of replays that it's executed
        // so far. It knows which range the host is currently watching, and if it further executes anything within that
        // range, it notifies the host.
        // HOST: The host builds up a database of replays, a database that persists across multiple client instances.
        // For each entry in that database it assigns a tracking ID. It ensures the editor has the same database,
        // by notifying the editor of each removed/added entry and associated ID. It knows which range the editor
        // is currently watching. If the range changes, by assumption the editor already knows the host's database.
        // The host also tells the client about the new range
        // 
        // Host builds up a database of ranges it knows, and hashes+text of the replays within that range,
        // and a synthesized ID for each replay.
        // Client 

        // Host keeps a list of ranges it knows, and hashes+texts of the replays within that range.
        // Assumption is that the client knows everything that the host does.
        // When editor requests a range, host delivers what it can and requests the rest from the client
        // When host requests a range, it sends all the lines within that range that it knows;
        // client will reply back by removing and adding as necessary.
        // When client updates asynchronously, it will only send back if within the watched range.
        

        // > WATCH file line count nhashes line1 hash1 ... lineN hashN
        // < REPLAY add line hash txt
        // < REPLAY remove line hash
        // > DUMP
        // < DUMP file line txt

        static TextWriter SystemOut = Console.Out;
        static TextReader SystemIn = Console.In;
        static HookOut MyOut = new HookOut();
        static HookIn MyIn = new HookIn();
        static Dictionary<string, Dictionary<int, LineItem>> Database = new Dictionary<string, Dictionary<int, LineItem>>();
        static BufferBlock<LineItem> Queue = new BufferBlock<LineItem>();

        private struct LineItem
        {
            public readonly string File;
            public readonly int Line;
            public readonly string Text;
            public readonly int TextHash;
            
            public LineItem(string file, int line, string text)
            {
                File = file; Line = line; Text = text; TextHash = text?.GetHashCode() ?? 0;
            }
        }

        static Replay()
        {
            Console.SetOut(MyOut);
            Console.SetIn(MyIn);
            var thread = new Thread(RunConversation);
            thread.IsBackground = false;
            thread.Start();
        }

        private class HookOut : TextWriter
        {
            StringBuilder sb = new StringBuilder();
            public override Encoding Encoding => SystemOut.Encoding;
            public override void Write(char value) => sb.Append(value);
            public string Log() { if (sb.Length == 0) return null; var s = sb.ToString(); sb.Clear(); return s; }
        }

        private class HookIn : TextReader
        {
        }

        public static void RunConversation()
        {
            var queueTask = Queue.ReceiveAsync();
            var stdinTask = Task.Run(SystemIn.ReadLineAsync);
            string watchFile = null; int watchLine = -1, watchCount = -1;
            SystemOut.WriteLine("OK");
            while (true)
            {
                var winner = Task.WaitAny(queueTask, stdinTask);

                if (stdinTask.IsCompleted)
                {
                    var line = stdinTask.Result;
                    stdinTask = Task.Run(SystemIn.ReadLineAsync);
                    if (line == null) Environment.Exit(1);
                    var cmds = line.Split('\t').ToList();

                    if (cmds[0] == "FILES")
                    {
                        foreach (var kv in Database) { SystemOut.WriteLine($"FILE\t{kv.Key}"); }
                    }

                    else if (cmds[0] == "DUMP")
                    {
                        if (cmds.Count != 1)
                        {
                            SystemOut.WriteLine($"ERROR\tExpected 'DUMP', got '{line}'"); continue;
                        }
                        foreach (var kv1 in Database)
                        {
                            foreach (var kv2 in kv1.Value)
                            {
                                var lineItem = kv2.Value;
                                SystemOut.WriteLine($"DUMP\t{lineItem.File}\t{lineItem.Line}\t{lineItem.Text}");
                            }
                        }
                    }

                    else if (cmds[0] == "WATCH")
                    {
                        string file; int iline, icount, nhashes, ihash;
                        if (cmds.Count < 5
                            || (file = cmds[1]) == null
                            || !int.TryParse(cmds[2], out iline) || !int.TryParse(cmds[3], out icount)
                            || !int.TryParse(cmds[4], out nhashes)
                            || cmds.Count != nhashes*2 + 5)
                        {
                            SystemOut.WriteLine($"ERROR\tExpected 'WATCH file line count nhashes ...', got '{line}'"); continue;
                        }
                        var hostAlreadyHas = new Dictionary<int, int>();
                        for (int i=0; i<nhashes; i++)
                        {
                            if (!int.TryParse(cmds[5+i], out iline) || !int.TryParse(cmds[6+i], out ihash)) { SystemOut.WriteLine($"ERROR\twrong hash #{i} in '{line}'"); continue; }
                            hostAlreadyHas[iline] = ihash;
                        }
                        watchFile = file; watchLine = iline; watchCount = icount;
                        //
                        var hostWillHave = new Dictionary<int,LineItem>();
                        if (Database.ContainsKey(watchFile))
                        {
                            var dbfile = Database[watchFile];
                            foreach (var kv in dbfile)
                            {
                                if (kv.Key < watchLine || kv.Key >= watchLine + watchCount) continue;
                                hostWillHave[kv.Key] = kv.Value;
                            }
                        }
                        //
                        var toRemove = hostAlreadyHas;
                        var toAdd = new List<LineItem>();
                        foreach (var kv in hostWillHave)
                        {
                            int hash; bool wasLineInHost = toRemove.TryGetValue(kv.Key, out hash);
                            if (wasLineInHost && hash == kv.Value.TextHash) { toRemove.Remove(kv.Key); continue; } // no need for update
                            toAdd.Add(kv.Value);
                        }
                        foreach (var kv in toRemove) SystemOut.WriteLine($"REPLAY\tremove\t{kv.Key}\t{kv.Value}");
                        foreach (var li in toAdd) SystemOut.WriteLine($"REPLAY\tadd\t{li.Line}\t{li.TextHash}\t{li.Text}");
                    }

                    else
                    {
                        SystemOut.WriteLine($"ERROR\tClient expected one FILES|DUMP|WATCH, got '{line}'");
                    }

                    continue;
                }

                if (queueTask.IsCompleted)
                {
                    var li = queueTask.Result;
                    queueTask = Queue.ReceiveAsync();
                    if (!Database.ContainsKey(li.File)) Database[li.File] = new Dictionary<int, LineItem>();
                    var dbfile = Database[li.File];
                    dbfile[li.Line] = li;
                    //
                    if (watchFile == li.File && watchLine <= li.Line && watchLine + watchCount > li.Line)
                    {
                        SystemOut.WriteLine($"REPLAY\tadd\t{li.Line}\t{li.TextHash}\t{li.Text}");
                    }
                }
            }
        }


        public static T Log<T>(T data, string id, string file, int line, int reason)
        {
            // Empty arguments is used purely to ensure the static constructor of Replay has been run
            if (data == null && id == null && file == null) return default(T);

            // Gather all the pending Console.Writes from the app, via our hooked side-effect Console.Out monoid
            var s = MyOut.Log()?.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
            if (s != null) Queue.Post(new LineItem(file, line, $"\"{s}\""));

            // Log this current event (for declarations and expression statements)
            s = (id == null) ? "" : id + "=";
            s += (data == null) ? "null" : data.ToString();
            if (reason == 1 || reason == 2) Queue.Post(new LineItem(file, line, s));

            return data;
        }
    }
}
