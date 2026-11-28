using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Qaplay.Runner
{
    /// <summary>
    /// 배치 검사(layout)의 예외 목록. 의도된 겹침과 넘침을 사유와 함께 적어 두면 그 뒤의 검사에서 빠진다.
    /// 프로젝트 저장소에 두는 파일 하나다(기본 qaplay-layout-exceptions.json). 한 줄에 예외 하나라서 바뀐 것을 줄 단위로 검토할 수 있다.
    /// 넘침은 종류와 요소의 경로로, 겹침은 종류와 두 요소의 경로(순서는 따지지 않는다)로 같은 문제인지 가린다.
    /// </summary>
    // ponytail: 경로는 요소의 이름을 이은 것이라, 이름이 같은 형제(목록의 줄들)는 한 예외로 함께 빠진다.
    // 화면을 가리지 않는다: 같은 경로의 문제는 어느 화면에서 나오든 빠진다.
    sealed class LayoutExceptions
    {
        public const string DefaultFile = "qaplay-layout-exceptions.json";

        public readonly string File;
        public readonly List<Dictionary<string, object>> Items = new List<Dictionary<string, object>>();

        LayoutExceptions(string file)
        {
            File = file;
        }

        public static LayoutExceptions Load(Args args)
        {
            var exceptions = new LayoutExceptions(args.Get("exceptions", DefaultFile));
            if (!System.IO.File.Exists(exceptions.File)) return exceptions;
            try
            {
                foreach (object item in (List<object>)Json.Parse(System.IO.File.ReadAllText(exceptions.File)))
                {
                    var entry = (Dictionary<string, object>)item;
                    if (!(entry["kind"] is string) || !(entry["path"] is string)) throw new FormatException("kind and path must be text");
                    exceptions.Items.Add(entry);
                }
            }
            catch (Exception e) when (e is FormatException || e is InvalidCastException || e is KeyNotFoundException)
            {
                // 깨진 예외 파일을 빈 목록으로 읽으면 예외가 모두 사라진 것처럼 검사가 실패한다. 파일이 문제임을 바로 알린다
                throw new UsageException("cannot read " + exceptions.File + ": " + e.Message);
            }
            return exceptions;
        }

        public void Save()
        {
            var text = new StringBuilder("[\n");
            for (int n = 0; n < Items.Count; n++) text.Append(' ').Append(Json.Write(Items[n])).Append(n < Items.Count - 1 ? ",\n" : "\n");
            string folder = Path.GetDirectoryName(Path.GetFullPath(File));
            Directory.CreateDirectory(folder);
            System.IO.File.WriteAllText(File, text.Append("]\n").ToString());
        }

        /// <summary>이 문제를 덮는 예외가 몇 번째인지(0부터). 없으면 -1.</summary>
        public int IndexOf(Dictionary<string, object> issue)
        {
            string key = Key(issue);
            return Items.FindIndex(item => Key(item) == key);
        }

        /// <summary>layout 명령의 결과에서 예외로 등록된 문제를 뺀다. 뺀 수를 돌려준다.</summary>
        public int Filter(Dictionary<string, object> result)
        {
            var issues = (List<object>)result["issues"];
            return issues.RemoveAll(issue => IndexOf((Dictionary<string, object>)issue) >= 0);
        }

        /// <summary>문제 하나를 예외로 등록한다. 이미 있으면 사유만 바꾼다.</summary>
        public void Add(Dictionary<string, object> issue, string reason)
        {
            int at = IndexOf(issue);
            // 파일에는 같은 문제인지 가리는 데 쓰는 것과 사유만 남긴다. 화면 좌표 같은 것은 화면마다 달라진다
            var entry = new Dictionary<string, object> { ["kind"] = issue["kind"], ["path"] = issue["path"] };
            if (issue.TryGetValue("otherPath", out object other)) entry["otherPath"] = other;
            entry["reason"] = reason;
            if (at >= 0) Items[at] = entry;
            else Items.Add(entry);
        }

        static string Key(Dictionary<string, object> entry)
        {
            string kind = (string)entry["kind"], path = (string)entry["path"];
            if (!entry.TryGetValue("otherPath", out object value) || !(value is string other)) return kind + "\n" + path;
            // 겹친 둘의 순서는 화면을 읽는 순서일 뿐이다
            return string.CompareOrdinal(path, other) <= 0 ? kind + "\n" + path + "\n" + other : kind + "\n" + other + "\n" + path;
        }

        /// <summary>예외 하나를 한 줄로.</summary>
        public static string Describe(Dictionary<string, object> entry) =>
            entry["kind"] + ": " + entry["path"] + (entry.TryGetValue("otherPath", out object other) ? " ~ " + other : "")
            + "  -- " + (entry.TryGetValue("reason", out object reason) ? reason : "");
    }

    /// <summary>qaplay exceptions ...: 예외 목록을 보고, 등록하고, 사유를 고치고, 지운다.</summary>
    static class ExceptionCommands
    {
        const string Usage =
            "usage:\n" +
            "  qaplay exceptions list                        show the registered exceptions, numbered\n" +
            "  qaplay exceptions add <issue>... --reason TEXT  register issues of the current screen; the numbers are the ones\n" +
            "                                                qaplay layout prints (the game must be on that screen)\n" +
            "  qaplay exceptions set <number> --reason TEXT  change the reason of an exception\n" +
            "  qaplay exceptions delete <number>...          remove exceptions\n" +
            "  [--exceptions FILE]                           the exception file (default " + LayoutExceptions.DefaultFile + ")";

        public static int Run(Args args)
        {
            LayoutExceptions exceptions = LayoutExceptions.Load(args);
            switch (args.Paths.Count > 0 ? args.Paths[0] : "")
            {
                case "list":
                    if (exceptions.Items.Count == 0) Console.WriteLine("no exceptions in " + exceptions.File);
                    for (int n = 0; n < exceptions.Items.Count; n++)
                        Console.WriteLine((n + 1) + ". " + LayoutExceptions.Describe(exceptions.Items[n]));
                    if (args.Json)
                        Program.PrintJson(new Dictionary<string, object> { ["file"] = exceptions.File, ["exceptions"] = exceptions.Items });
                    return 0;

                case "add":
                {
                    string reason = Reason(args);
                    List<int> numbers = Numbers(args);
                    // qaplay layout이 보여 주는 것과 같은 목록: 지금 화면의 문제에서 이미 예외인 것을 뺀 것
                    using AgentClient client = Program.Connect(args);
                    Dictionary<string, object> result = Program.Ok(client.Request("layout", null));
                    exceptions.Filter(result);
                    var issues = (List<object>)result["issues"];
                    foreach (int number in numbers)
                        if (number > issues.Count)
                            throw new UsageException("there is no issue " + number + " on the current screen (qaplay layout shows " + issues.Count + ")");
                    foreach (int number in numbers)
                    {
                        var issue = (Dictionary<string, object>)issues[number - 1];
                        exceptions.Add(issue, reason);
                        Console.WriteLine("added: " + LayoutExceptions.Describe(exceptions.Items[exceptions.IndexOf(issue)]));
                    }
                    exceptions.Save();
                    return 0;
                }

                case "set":
                {
                    string reason = Reason(args);
                    List<int> numbers = Numbers(args);
                    if (numbers.Count != 1) throw new UsageException("set needs exactly one exception number");
                    Entry(exceptions, numbers[0])["reason"] = reason;
                    exceptions.Save();
                    Console.WriteLine(numbers[0] + ". " + LayoutExceptions.Describe(exceptions.Items[numbers[0] - 1]));
                    return 0;
                }

                case "delete":
                {
                    List<int> numbers = Numbers(args);
                    foreach (int number in numbers) Entry(exceptions, number);
                    // 뒤에서부터 지워야 앞의 번호가 밀리지 않는다
                    numbers.Sort();
                    for (int n = numbers.Count - 1; n >= 0; n--)
                    {
                        if (n < numbers.Count - 1 && numbers[n] == numbers[n + 1]) continue;
                        Console.WriteLine("deleted: " + LayoutExceptions.Describe(exceptions.Items[numbers[n] - 1]));
                        exceptions.Items.RemoveAt(numbers[n] - 1);
                    }
                    exceptions.Save();
                    return 0;
                }

                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }

        static Dictionary<string, object> Entry(LayoutExceptions exceptions, int number) =>
            number <= exceptions.Items.Count
                ? exceptions.Items[number - 1]
                : throw new UsageException("there is no exception " + number + " in " + exceptions.File + " (it has " + exceptions.Items.Count + ")");

        // 사유 없는 예외는 받지 않는다. 왜 넘어가기로 했는지를 다음 사람이 알 수 있어야 한다
        static string Reason(Args args)
        {
            string reason = args.Get("reason", null);
            if (string.IsNullOrWhiteSpace(reason)) throw new UsageException("say why with --reason \"...\"");
            return reason.Trim();
        }

        static List<int> Numbers(Args args)
        {
            var numbers = new List<int>();
            for (int n = 1; n < args.Paths.Count; n++)
            {
                if (!int.TryParse(args.Paths[n], out int number) || number < 1) throw new UsageException("not a number: " + args.Paths[n]);
                numbers.Add(number);
            }
            if (numbers.Count == 0) throw new UsageException("this command needs a number (see qaplay layout or qaplay exceptions list)");
            return numbers;
        }
    }
}
