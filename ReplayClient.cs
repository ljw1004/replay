using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace System.Runtime.CompilerServices
{
    public class Replay
    {
        static TextWriter SystemOut = Console.Out;
        static TextReader SystemIn = Console.In;
        static HookOut MyOut = new HookOut();
        static HookIn MyIn = new HookIn();
        static Dictionary<string, SortedDictionary<int, string>> Database = new Dictionary<string, SortedDictionary<int, string>>();
        static BufferBlock<Tuple<string, int, string>> Channel = new BufferBlock<Tuple<string, int, string>>();

        static Replay()
        {
            Console.SetOut(MyOut);
            Console.SetIn(MyIn);
            var thread = new Threading.Thread(RunConversation);
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
            var channelTask = Channel.ReceiveAsync();
            var lineTask = Task.Run(SystemIn.ReadLineAsync);
            string watchFile = null;
            int watchLineStart = -1, watchLineLength = -1;
            SystemOut.WriteLine("OK");
            while (true)
            {
                var winner = Task.WaitAny(channelTask, lineTask);
                if (lineTask.IsCompleted)
                {
                    var line = lineTask.Result;
                    lineTask = Task.Run(SystemIn.ReadLineAsync);
                    if (line == null) Environment.Exit(1);
                    var cmds = line.Split('\t').ToList();
                    if (cmds[0] == "files")
                    {
                        foreach (var file in Database.Keys) SystemOut.WriteLine($"-1:{file}");
                    }
                    else if (cmds[0] == "dump" || cmds[0] == "database")
                    {
                        foreach (var kv in Database)
                        {
                            SystemOut.WriteLine($"-1:{kv.Key}");
                            foreach (var lm in kv.Value)
                            {
                                SystemOut.WriteLine($"{lm.Key}:{lm.Value}");
                            }
                        }
                    }
                    else if (cmds[0] == "watch" || cmds[0] == "missing")
                    {
                        HashSet<int> alreadyGot;
                        if (watchLineStart == -1) alreadyGot = new HashSet<int>();
                        else alreadyGot = new HashSet<int>(Enumerable.Range(watchLineStart, watchLineLength));
                        //
                        if (cmds[0] == "watch") { watchFile = cmds[1]; watchLineStart = int.Parse(cmds[2]); watchLineLength = int.Parse(cmds[3]); cmds.RemoveRange(0, 4); }
                        //
                        HashSet<int> need;
                        if (watchLineStart == -1) need = new HashSet<int>();
                        else need = new HashSet<int>(Enumerable.Range(watchLineStart, watchLineLength));
                        //
                        if (cmds.Count > 0 && cmds[0] == "missing")
                        {
                            for (int i = 1; i < cmds.Count; i++) alreadyGot.Remove(int.Parse(cmds[i]));
                        }
                        //
                        need.ExceptWith(alreadyGot);
                        if (Database.ContainsKey(watchFile))
                        {
                            foreach (var kv in Database[watchFile])
                            {
                                if (need.Contains(kv.Key)) SystemOut.WriteLine($"{kv.Key}:{kv.Value}");
                            }
                        }
                    }
                    else
                    {
                        SystemOut.WriteLine($"client can't parse command '{line.Replace("\\","\\\\").Replace("\t","\\t").Replace("\n","\\n").Replace("\r","\\r").Replace("\0","\\0")}'");
                    }
                }
                if (channelTask.IsCompleted)
                {
                    var t = channelTask.Result;
                    channelTask = Channel.ReceiveAsync();
                    if (!Database.ContainsKey(t.Item1)) Database[t.Item1] = new SortedDictionary<int, string>();
                    Database[t.Item1][t.Item2] = t.Item3;
                    if (t.Item1 == watchFile && t.Item2 >= watchLineStart && t.Item2 < watchLineStart + watchLineLength) SystemOut.WriteLine($"{t.Item2}:{t.Item3}");
                }
            }
        }


        public static T Log<T>(T data, string id, string file, int line, int reason)
        {
            if (data == null && id == null && file == null) return default(T);
            var s = MyOut.Log()?.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
            if (s != null) Channel.Post(Tuple.Create(file, line, $"\"{s}\""));
            if (id != null) id = id + "=";
            if (reason == 1 || reason == 2) Channel.Post(Tuple.Create(file, line, $"{id}{ (data == null ? "null" : data.ToString()) }"));
            return data;
        }
    }
}
