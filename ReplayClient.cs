﻿using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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

        static Channel<LineItem> Queue = new Channel<LineItem>();

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
            public LineItem WithContent(string content)
            {
                return new LineItem(File, Line, content);
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
            var watchHashes = new Dictionary<string, Dictionary<int, int>>();

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
                        string file, correlation; int line=-1, count=-1;
                        if ((cmds.Length != 3 && cmds.Length < 5)
                            || (correlation = cmds[1]) == null
                            || (file = cmds[2]) == null
                            || (cmds.Length>3 && !int.TryParse(cmds[3], out line))
                            || (cmds.Length>3 && !int.TryParse(cmds[4], out count)))
                        {
                            SystemOut.WriteLine($"ERROR\tExpected 'WATCH correlation file line count <hashes>', got '{cmd}'"); continue;
                        }

                        // Parse the watch command
                        watchFile = file; watchLine = line; watchCount = count;
                        string hashFile = null;
                        for (int i=5; i<cmds.Length-1;)
                        {
                            int hline, hhash;
                            if (int.TryParse(cmds[i], out hline) && int.TryParse(cmds[i+1], out hhash))
                            {
                                watchHashes[hashFile][hline] = hhash;
                                i += 2;
                            }
                            else
                            {
                                hashFile = cmds[i];
                                if (!watchHashes.ContainsKey(hashFile)) watchHashes[hashFile] = new Dictionary<int, int>();
                                i += 1;
                            }
                        }

                        // Send out a dump, if needed
                        foreach (var dbkv in Database)
                        {
                            if (watchFile != "*" && watchFile != dbkv.Key) continue;
                            if (!watchHashes.ContainsKey(dbkv.Key)) watchHashes[dbkv.Key] = new Dictionary<int, int>();
                            var watchFileHashes = watchHashes[dbkv.Key];
                            foreach (var kv in dbkv.Value)
                            {
                                var li = kv.Value; int hash;
                                if (watchLine == -1 && watchCount == -1) { }
                                else if (li.Line < watchLine || li.Line >= watchLine + watchCount) continue;
                                if (watchFileHashes.TryGetValue(li.Line, out hash) && hash == li.ContentHash) continue;
                                SystemOut.WriteLine($"REPLAY\tadd\t{li.File}\t{li.Line}\t{li.ContentHash}\t{li.Content}");
                                watchFileHashes[li.Line] = li.ContentHash;
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
                    LineItem oldli; if (dbfile.TryGetValue(li.Line, out oldli))
                    {
                        var c = oldli.Content + " " + li.Content;
                        if (c.Length > 54)
                        {
                            c = "... " + c.Substring(c.Length - 54).Replace("\\r", "").Replace("\\n", "");
                            if (c.StartsWith("... ... ")) c = c.Substring(4);
                        }
                        li = li.WithContent(c);
                    }
                    dbfile[li.Line] = li;

                    if (watchFile != "*" && watchFile != li.File) continue;
                    if (watchLine == -1 && watchCount == -1) { }
                    else if (watchLine <= li.Line && li.Line < watchLine + watchCount) { }
                    else continue;
                    if (!watchHashes.ContainsKey(li.File)) watchHashes[li.File] = new Dictionary<int, int>();
                    int hash; if (watchHashes[li.File].TryGetValue(li.Line, out hash) && hash == li.ContentHash) continue;

                    var s = li.Content;
                    SystemOut.WriteLine($"REPLAY\tadd\t{li.File}\t{li.Line}\t{li.ContentHash}\t{li.Content}");
                    watchHashes[li.File][li.Line] = li.ContentHash;
                    continue;
                }

                if (endTask?.IsCompleted == true)
                {
                    endTask = new TaskCompletionSource<object>().Task; // hacky way prevent it ever firing again
                    foreach (var dbkv in Database)
                    {
                        if (watchFile != "*" && !Database.ContainsKey(dbkv.Key)) continue;
                        if (!watchHashes.ContainsKey(dbkv.Key)) watchHashes[dbkv.Key] = new Dictionary<int, int>();
                        var dbLines = new HashSet<int>(dbkv.Value.Keys);
                        var watchLines = new HashSet<int>(watchHashes[dbkv.Key].Keys);
                        foreach (var line in dbLines.Except(watchLines)) SystemOut.WriteLine($"ERROR\tUpon exit, expected '{dbkv.Key}:({line})' to have adornment, but watcher has nothing");
                        foreach (var line in watchLines.Except(dbLines)) SystemOut.WriteLine($"REPLAY\tremove\t{dbkv.Key}\t{line}");
                        foreach (var line in dbLines.Intersect(watchLines).Where(i => dbkv.Value[i].ContentHash != watchHashes[dbkv.Key][i])) SystemOut.WriteLine($"ERROR\tUpon exit, expected file '{dbkv.Key}:({line})' to have hash {dbkv.Value[line].ContentHash} but watcher has hash {watchHashes[dbkv.Key][line]}");
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


        private class Channel<T>
        {
            Queue<T> posts = new Queue<T>();
            Queue<TaskCompletionSource<T>> recvs = new Queue<TaskCompletionSource<T>>();

            public Task<T> ReceiveAsync()
            {
                lock (posts)
                {
                    if (posts.Count > 0) return Task.FromResult(posts.Dequeue());
                    else { var tcs = new TaskCompletionSource<T>(); recvs.Enqueue(tcs); return tcs.Task; }
                }
            }

            public void Post(T value)
            {
                lock (posts)
                {
                    if (recvs.Count > 0) recvs.Dequeue().TrySetResult(value);
                    else posts.Enqueue(value);
                }
            }
        }



    }
}
