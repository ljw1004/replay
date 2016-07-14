using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace System.Runtime.CompilerServices
{
    public static class Replay
    {
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


        static TextWriter SystemOut = Console.Out;
        static TextReader SystemIn = Console.In;
        static HookOut MyOut = new HookOut();
        static HookIn MyIn = new HookIn();

        static BufferBlock<LineItem> Queue = new BufferBlock<LineItem>();

        private struct LineItem
        {
            public readonly string File;
            public readonly int Line;
            public readonly string Content;
            public readonly int ContentHash;
            
            public LineItem(string file, int line, string content)
            {
                File = file; Line = line; Content = content; ContentHash = content?.GetStableHashCode() ?? 0;
            }
        }

        static Replay()
        {
            Console.SetOut(MyOut);
            Console.SetIn(MyIn);
            var thread = new Thread(RunConversation);
            thread.IsBackground = false;
            thread.Start(Thread.CurrentThread);
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

        public static void RunConversation(object mainThread)
        {
            var queueTask = Queue.ReceiveAsync();
            var stdinTask = Task.Run(SystemIn.ReadLineAsync);
            var endTask = Task.Run(async () => {
                while (true) { await Task.Delay(200); if (!(mainThread as Thread).IsAlive) return; }
            });
            SystemOut.WriteLine("OK");

            // This is the state of the client
            var Database = new Dictionary<string, Dictionary<int, LineItem>>();
            string watchFile = null;
            int watchLine = -1, watchCount = -1;
            var watchHashes = new Dictionary<int, int>();

            while (true)
            {
                Task.WaitAny(queueTask, stdinTask, endTask);

                if (stdinTask.IsCompleted)
                {
                    var cmd = stdinTask.Result; stdinTask = Task.Run(SystemIn.ReadLineAsync);
                    if (cmd == null) Environment.Exit(1);
                    var cmds = cmd.Split('\t');

                    if (cmds[0] == "FILES")
                    {
                        if (cmds.Length != 1)
                        {
                            SystemOut.WriteLine($"ERROR\tEXPECTED 'FILES', got '{cmd}'"); continue;
                        }
                        foreach (var kv in Database) { SystemOut.WriteLine($"FILE\t{kv.Key}"); }
                    }

                    else if (cmds[0] == "DUMP")
                    {
                        if (cmds.Length != 1)
                        {
                            SystemOut.WriteLine($"ERROR\tExpected 'DUMP', got '{cmd}'"); continue;
                        }
                        foreach (var kv1 in Database)
                        {
                            foreach (var kv2 in kv1.Value)
                            {
                                var lineItem = kv2.Value;
                                SystemOut.WriteLine($"DUMP\t{lineItem.File}\t{lineItem.Line}\t{lineItem.Content}");
                            }
                        }
                    }

                    else if (cmds[0] == "WATCH")
                    {
                        string file, correlation; int line, count, nhashes;
                        if (cmds.Length < 6
                            || (correlation = cmds[1]) == null
                            || (file = cmds[2]) == null
                            || !int.TryParse(cmds[3], out line)
                            || !int.TryParse(cmds[4], out count)
                            || !int.TryParse(cmds[5], out nhashes)
                            || cmds.Length != nhashes*2 + 6)
                        {
                            SystemOut.WriteLine($"ERROR\tExpected 'WATCH correlation file line count nhashes ...', got '{cmd}'"); continue;
                        }

                        watchFile = file; watchLine = line; watchCount = count;

                        for (int i=0; i<nhashes*2; i+=2)
                        {
                            int hash;
                            if (!int.TryParse(cmds[6+i], out line) || !int.TryParse(cmds[7+i], out hash)) { SystemOut.WriteLine($"ERROR\twrong hash #{i} in '{cmd}'"); continue; }
                            watchHashes[line] = hash;
                        }

                        if (watchFile != null && Database.ContainsKey(watchFile))
                        {
                            var dbfile = Database[watchFile];
                            foreach (var kv in dbfile)
                            {
                                var li = kv.Value; int hash;
                                if (li.Line < watchLine || li.Line >= watchLine + watchCount) continue;
                                if (watchHashes.TryGetValue(li.Line, out hash) && hash == li.ContentHash) continue;
                                SystemOut.WriteLine($"REPLAY\tadd\t{li.Line}\t{li.ContentHash}\t{li.Content}");
                                watchHashes[li.Line] = li.ContentHash;
                            }
                        }
                        if (correlation != "") SystemOut.WriteLine($"END\twatch\t{correlation}");
                    }

                    else
                    {
                        SystemOut.WriteLine($"ERROR\tClient expected one FILES|DUMP|WATCH, got '{cmd}'");
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

                    if (watchFile == li.File && watchLine <= li.Line && li.Line < watchLine + watchCount)
                    {
                        int hash; if (!watchHashes.TryGetValue(li.Line, out hash) || hash != li.ContentHash)
                        {
                            var s = li.Content;
                            SystemOut.WriteLine($"DEBUG\tsending content='{li.Content}' hash='{li.ContentHash}'");
                            SystemOut.WriteLine($"REPLAY\tadd\t{li.Line}\t{li.ContentHash}\t{li.Content}");
                            watchHashes[li.Line] = li.ContentHash;
                        }
                    }
                    continue;
                }

                if (endTask?.IsCompleted == true)
                {
                    endTask = new TaskCompletionSource<object>().Task; // hacky way prevent it ever firing again
                    if (watchFile != null && Database.ContainsKey(watchFile))
                    {
                        var dbfile = Database[watchFile];
                        for (int line = watchLine; line < watchLine + watchCount; line++)
                        {
                            LineItem li;
                            bool hasLi = dbfile.TryGetValue(line, out li);
                            int hash; bool hasWatch = watchHashes.TryGetValue(line, out hash);
                            if (hasLi && hasWatch && hash == li.ContentHash) { }
                            else if (hasLi && hasWatch && hash != li.ContentHash) SystemOut.WriteLine($"ERROR\tUpon exit, expected file '{watchFile}:({line})' to have hash {li.ContentHash} but watcher has hash {hash}");
                            else if (hasLi && !hasWatch) SystemOut.WriteLine($"ERROR\tUpon exit, expected file '{watchFile}:({line})' to have hash {li.ContentHash} but watcher has nothing");
                            else if (!hasLi && hasWatch) SystemOut.WriteLine($"REPLAY\tremove\t{line}");
                            else if (!hasLi && !hasWatch) { }
                        }
                    }
                    SystemOut.WriteLine($"END\trun");
                }

            }
        }


        private static int GetStableHashCode(this string str)
        {
            unchecked
            {
                int hash1 = (5381 << 16) + 5381;
                int hash2 = hash1;

                for (int i = 0; i < str.Length; i += 2)
                {
                    hash1 = ((hash1 << 5) + hash1) ^ str[i];
                    if (i == str.Length - 1)
                        break;
                    hash2 = ((hash2 << 5) + hash2) ^ str[i + 1];
                }

                return hash1 + (hash2 * 1566083941);
            }
        }

    }
}
