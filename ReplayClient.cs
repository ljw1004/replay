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
        // < STAMP stamp
        // > WATCH file line count stamp
        // < REPLAY add id line txt
        // < REPLAY remove id
        // > DUMP stamp
        // < DUMP file line txt

        static TextWriter SystemOut = Console.Out;
        static TextReader SystemIn = Console.In;
        static HookOut MyOut = new HookOut();
        static HookIn MyIn = new HookIn();
        static Dictionary<string, Dictionary<int, LinkedList<LineItem>>> Database = new Dictionary<string, Dictionary<int, LinkedList<LineItem>>>();
        static BufferBlock<LineItem> Queue = new BufferBlock<LineItem>();
        static HashSet<LineItem> CurrentLines = new HashSet<LineItem>();
        static long Stamp = 0;
        static long Id = 0;

        private struct LineItem
        {
            public long Id;
            public string File;
            public int Line;
            public long Stamp;
            public string Text;
            public LineItem WithId(long id) => new LineItem { Id = id, File = File, Line = Line, Stamp = Stamp, Text = Text };
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

        private static LineItem? GetLineItem(LinkedList<LineItem> lineItems, long stamp)
        {
            if (lineItems == null || lineItems.Count == 0) return null;
            if (stamp == 0) return lineItems.Last.Value;
            LinkedListNode<LineItem> node = null;
            for (node = lineItems.Last; node != null; node = node.Previous)
            {
                if (node.Value.Stamp <= stamp) break;
            }
            return node?.Value;
        }

        class LineItemComparer : IEqualityComparer<LineItem>
        {
            public bool Equals(LineItem x, LineItem y) => x.File == y.File && x.Line == y.Line && x.Stamp == y.Stamp;
            public int GetHashCode(LineItem x) => unchecked (((17 * 23 + x.File.GetHashCode()) * 23 + x.Line) * 23 + x.Stamp.GetHashCode());
        }
        static LineItemComparer Comparer = new LineItemComparer();

        public static void RunConversation()
        {
            var queueTask = Queue.ReceiveAsync();
            var stdinTask = Task.Run(SystemIn.ReadLineAsync);
            string watchFile = null; int watchLine = -1, watchCount = -1; long watchStamp = -1;
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
                        long stamp = 0;
                        if ((cmds.Count != 1 && cmds.Count != 2)
                            || (cmds.Count == 2 && !long.TryParse(cmds[1], out stamp)))
                        {
                            SystemOut.WriteLine($"ERROR\tExpected 'DUMP [stamp]', got '{line}'"); continue;
                        }
                        foreach (var kv1 in Database)
                        {
                            foreach (var kv2 in kv1.Value)
                            {
                                var lineItem = GetLineItem(kv2.Value, stamp);
                                if (lineItem.HasValue) SystemOut.WriteLine($"DUMP\t{lineItem.Value.File}\t{lineItem.Value.Line}\t{lineItem.Value.Stamp}\t{lineItem.Value.Text}");
                            }
                        }
                    }

                    else if (cmds[0] == "WATCH")
                    {
                        string file; int iline, icount; long stamp = 0;
                        if ((cmds.Count != 4 && cmds.Count != 5)
                            || (file = cmds[1]) == null
                            || !int.TryParse(cmds[2], out iline) || !int.TryParse(cmds[3], out icount)
                            || (cmds.Count == 5 && (!long.TryParse(cmds[4], out stamp) || stamp >= Stamp)))
                        {
                            SystemOut.WriteLine($"ERROR\tExpected 'WATCH file line count [stamp]', got '{line}'"); continue;
                        }
                        watchFile = file; watchLine = iline; watchCount = icount; watchStamp = stamp;
                        //
                        var newLines = new HashSet<LineItem>();
                        if (Database.ContainsKey(watchFile))
                        {
                            var dbfile = Database[watchFile];
                            foreach (var kv in dbfile)
                            {
                                if (kv.Key < watchLine || kv.Key >= watchLine + watchCount) continue;
                                var lineItem = GetLineItem(kv.Value, stamp);
                                if (lineItem.HasValue) newLines.Add(lineItem.Value.WithId(Id++));
                            }
                        }
                        //
                        var toRemove = CurrentLines.Except(newLines, Comparer);
                        var toAdd = newLines.Except(CurrentLines, Comparer);
                        foreach (var li in toRemove)
                        {
                            SystemOut.WriteLine($"REPLAY\tremove\t{li.Id}");
                        }
                        foreach (var li in toAdd)
                        {
                            SystemOut.WriteLine($"REPLAY\tadd\t{li.Id}\t{li.Line}\t{li.Text}");
                        }
                        CurrentLines = newLines;

                    }

                    else
                    {
                        SystemOut.WriteLine($"ERROR\tClient expected one FILES|DUMP|WATCH, got '{line}'");
                    }

                    continue;
                }

                if (queueTask.IsCompleted)
                {
                    // TODO: should ID be added to the lineitem here rather than in Log<>?
                    var li = queueTask.Result;
                    queueTask = Queue.ReceiveAsync();
                    if (!Database.ContainsKey(li.File)) Database[li.File] = new Dictionary<int, LinkedList<LineItem>>();
                    var dbfile = Database[li.File];
                    if (!dbfile.ContainsKey(li.Line)) dbfile[li.Line] = new LinkedList<LineItem>();
                    var lineItems = dbfile[li.Line];
                    lineItems.AddLast(li);
                    //
                    if (watchStamp == 0 && watchFile == li.File && watchLine <= li.Line && watchLine + watchCount > li.Line)
                    {
                        SystemOut.WriteLine($"REPLAY\tadd\t{li.Id}\t{li.Line}\t{li.Text}");
                    }
                }
            }
        }


        public static T Log<T>(T data, string id, string file, int line, int reason)
        {
            // Empty arguments is used purely to ensure the static constructor of Replay has been run
            if (data == null && id == null && file == null) return default(T);

            long stamp = Interlocked.Increment(ref Stamp);

            // Gather all the pending Console.Writes from the app, via our hooked side-effect Console.Out monoid
            var s = MyOut.Log()?.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
            if (s != null) Queue.Post(new LineItem { File = file, Line = line, Stamp = stamp, Text = $"\"{s}\"" });

            // Log this current event (for declarations and expression statements)
            s = (id == null) ? "" : id + "=";
            s += (data == null) ? "null" : data.ToString();
            if (reason == 1 || reason == 2) Queue.Post(new LineItem { File = file, Line = line, Stamp = stamp, Text = s });

            return data;
        }
    }
}
