﻿using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace System.Runtime.CompilerServices
{
    public class Replay
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
                File = file; Line = line; Content = content; ContentHash = content?.GetHashCode() ?? 0;
            }
        }

        static Replay()
        {
            Console.SetOut(MyOut);
            Console.SetIn(MyIn);
            var thread = new Thread(RunConversation);
            thread.IsBackground = false;
            thread.Start();
            OnExit(() => Queue.Post(new LineItem("ONEXIT", -1, null)));
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
            SystemOut.WriteLine("OK");

            // This is the state of the client
            var Database = new Dictionary<string, Dictionary<int, LineItem>>();
            string watchFile = null;
            int watchLine = -1, watchCount = -1;
            var watchHashes = new Dictionary<int, int>();


            while (true)
            {
                Task.WaitAny(queueTask, stdinTask);

                if (stdinTask.IsCompleted)
                {
                    var cmd = stdinTask.Result; stdinTask = Task.Run(SystemIn.ReadLineAsync);
                    if (cmd == null) Environment.Exit(1);
                    var cmds = cmd.Split('\t').ToList();

                    if (cmds[0] == "FILES")
                    {
                        if (cmds.Count != 1)
                        {
                            SystemOut.WriteLine($"ERROR\tEXPECTED 'FILES', got '{cmd}'"); continue;
                        }
                        foreach (var kv in Database) { SystemOut.WriteLine($"FILE\t{kv.Key}"); }
                    }

                    else if (cmds[0] == "DUMP")
                    {
                        if (cmds.Count != 1)
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
                        string file; int line, count, nhashes;
                        if (cmds.Count < 5
                            || (file = cmds[1]) == null
                            || !int.TryParse(cmds[2], out line) || !int.TryParse(cmds[3], out count)
                            || !int.TryParse(cmds[4], out nhashes)
                            || cmds.Count != nhashes*2 + 5)
                        {
                            SystemOut.WriteLine($"ERROR\tExpected 'WATCH file line count nhashes ...', got '{cmd}'"); continue;
                        }
                        for (int i=0; i<nhashes; i++)
                        {
                            int hash;
                            if (!int.TryParse(cmds[5+i], out line) || !int.TryParse(cmds[6+i], out hash)) { SystemOut.WriteLine($"ERROR\twrong hash #{i} in '{cmd}'"); continue; }
                            watchHashes[line]   = hash;
                        }
                        watchFile = file; watchLine = line; watchCount = count;
                        foreach (var k in watchHashes.Keys.Where(i => i < watchLine || i >= watchLine + watchCount).ToArray()) watchHashes.Remove(k);

                        if (Database.ContainsKey(watchFile))
                        {
                            var dbfile = Database[watchFile];
                            foreach (var kv in dbfile)
                            {
                                var li = kv.Value; int hash;
                                if (li.Line < watchLine || li.Line >= watchLine + watchCount) continue;
                                if (watchHashes.TryGetValue(li.Line, out hash) && hash == li.ContentHash) continue;
                                SystemOut.WriteLine($"REPLAY\tadd\t{li.Line}\t{li.ContentHash}\t{li.Content}");
                            }
                        }
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

                    if (li.File == "ONEXIT")
                    {
                        if (!Database.ContainsKey(watchFile)) Database[watchFile] = new Dictionary<int, LineItem>();
                        var dbfile = Database[watchFile];
                        for (int line=watchLine; line<watchLine+watchCount; line++)
                        {
                            bool hasLi = dbfile.TryGetValue(line, out li);
                            int hash; bool hasWatch = watchHashes.TryGetValue(line, out hash);
                            if (hasLi && hasWatch && hash == li.ContentHash) { }
                            else if (hasLi && hasWatch && hash != li.ContentHash) SystemOut.WriteLine($"ERROR\tUpon exit, expected file '{watchFile}:({line})' to have hash {li.ContentHash} but watcher has hash {hash}");
                            else if (hasLi && !hasWatch) SystemOut.WriteLine($"ERROR\tUpon exit, expected file '{watchFile}:({line})' to have hash {li.ContentHash} but watcher has nothing");
                            else if (!hasLi && hasWatch) SystemOut.WriteLine($"REPLAY\tremove\t{line}");
                            else if (!hasLi && !hasWatch) { }
                        }
                    }
                    else
                    {
                        if (!Database.ContainsKey(li.File)) Database[li.File] = new Dictionary<int, LineItem>();
                        var dbfile = Database[li.File];
                        dbfile[li.Line] = li;
                        if (watchFile == li.File && watchLine <= li.Line && watchLine + watchCount > li.Line)
                        {
                            int hash; if (!watchHashes.TryGetValue(li.Line, out hash) || hash != li.ContentHash)
                            {
                                SystemOut.WriteLine($"REPLAY\tadd\t{li.Line}\t{li.ContentHash}\t{li.Content}");
                            }
                        }
                    }
                }
            }
        }


        public static void OnExit(Action onExit)
        {
            var assemblyLoadContextType = Type.GetType("System.Runtime.Loader.AssemblyLoadContext, System.Runtime.Loader");
            if (assemblyLoadContextType != null)
            {
                var currentLoadContext = assemblyLoadContextType.GetTypeInfo().GetProperty("Default").GetValue(null, null);
                var unloadingEvent = currentLoadContext.GetType().GetTypeInfo().GetEvent("Unloading");
                var delegateType = typeof(Action<>).MakeGenericType(assemblyLoadContextType);
                Action<object> lambda = (context) => onExit();
                unloadingEvent.AddEventHandler(currentLoadContext, lambda.GetMethodInfo().CreateDelegate(delegateType, lambda.Target));
                return;
            }

            var appDomainType = Type.GetType("System.AppDomain, mscorlib");
            if (appDomainType != null)
            {
                var currentAppDomain = appDomainType.GetTypeInfo().GetProperty("CurrentDomain").GetValue(null, null);
                var processExitEvent = currentAppDomain.GetType().GetTypeInfo().GetEvent("ProcessExit");
                EventHandler lambda = (sender, e) => onExit();
                processExitEvent.AddEventHandler(currentAppDomain, lambda);
                return;
                // Note that .NETCore has a private System.AppDomain which lacks the ProcessExit event.
                // That's why we test for AssemblyLoadContext first!
            }


            bool isNetCore = (Type.GetType("System.Object, System.Runtime") != null);
            if (isNetCore) throw new Exception("Before calling this function, declare a variable of type 'System.Runtime.Loader.AssemblyLoadContext' from NuGet package 'System.Runtime.Loader'");
            else throw new Exception("Neither mscorlib nor System.Runtime.Loader is referenced");

        }

    }
}
