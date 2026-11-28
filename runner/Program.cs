using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Xml;

namespace Qaplay.Runner
{
    static class Program
    {
        public const int AgentPort = 27275;   // 게임 안의 에이전트가 기다리는 포트
        public const int RelayPort = 27276;   // serve가 Python 스크립트에 여는 포트

        const string Usage =
            "usage:\n" +
            "  qaplay run <rule.yaml | folder>...   run rules; exit code 0 = all passed (flaky counts as passed), 1 = failed\n" +
            "      [--retries N]   rerun a failed rule up to N times (default 1); passing on a rerun is reported as flaky\n" +
            "      [--junit FILE]  also write the results as JUnit XML\n" +
            "  qaplay check <rule.yaml>             check the selectors against the current screen without running\n" +
            "  qaplay dump                          list the elements on the current screen (what a rule can tap and assert)\n" +
            "  qaplay values                        list the game values a rule can read with assertValue\n" +
            "  qaplay memory                        show the memory the game uses now (what assertMemory compares, in MB)\n" +
            "      [--json]        run, check, dump, values, memory: print the result as JSON on stdout; messages go to stderr\n" +
            "  qaplay serve [--listen PORT] [-- command...]\n" +
            "                                       relay for Python scripts; with a command, run it and exit with its code\n" +
            "  qaplay devices                       list Android devices\n" +
            "  qaplay bugs ...                      bug reports: pull, report, list, show, set, delete, promote, verify (run: qaplay bugs)\n" +
            "      run registers exceptions, crashes and freezes as bugs in --bugs DIR (default qaplay-bugs; \"off\" to disable)\n" +
            "connection options (run, check, dump, values, serve):\n" +
            "  [--host HOST] [--port PORT]          where the game listens (default 127.0.0.1:27275)\n" +
            "  [--device SERIAL] [--adb PATH]       Android: forward the port to this device first\n" +
            "  [--freeze-seconds N]                 a game whose frames stop for N seconds is frozen (default 10)\n" +
            "let the runner start the game, so it can restart it after a crash or a freeze (run, serve):\n" +
            "  [--app GAME.EXE] [--app-args \"...\"]  a PC build\n" +
            "  [--package NAME]                     an Android app on --device\n" +
            "  [--artifacts DIR]                    where game logs are saved (default qaplay-artifacts)\n" +
            "a C# exception, a crash or a freeze fails the rule and is never rerun\n" +
            "exit code 2 = bad usage or the game could not be reached";

        // --json의 결과가 나가는 곳. Console.Out은 --json일 때 표준 오류로 바뀐다
        static TextWriter s_Stdout;

        static void PrintJson(object data) => s_Stdout.WriteLine(Json.Write(data));

        static int Main(string[] argv)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            s_Stdout = Console.Out;
            try
            {
                var args = new Args(argv);
                if (args.Json)
                {
                    if (Array.IndexOf(new[] { "run", "check", "dump", "values", "memory" }, args.Command) < 0)
                        throw new UsageException("--json is supported by run, check, dump, values and memory");
                    // 기계가 읽는 결과만 표준 출력으로 내보낸다. 사람이 읽는 줄은 모두 표준 오류로 간다
                    Console.SetOut(Console.Error);
                }
                switch (args.Command)
                {
                    case "run": return Run(args);
                    case "check": return Check(args);
                    case "dump": return Dump(args);
                    case "values": return Values(args);
                    case "memory": return Memory(args);
                    case "serve": return Serve(args);
                    case "devices": return Devices(args);
                    case "bugs": return BugCommands.Run(args);
                    case "selftest": return SelfTest.Run();
                    default:
                        Console.Error.WriteLine(Usage);
                        return 2;
                }
            }
            catch (Exception e) when (e is UsageException || e is IOException || e is SocketException)
            {
                Console.Error.WriteLine("qaplay: " + e.Message);
                return 2;
            }
        }

        // ---- run ----

        static int Run(Args args)
        {
            var total = Stopwatch.StartNew();
            List<Outcome> outcomes = Execute(args, RuleFiles(args.Paths), out Dictionary<string, object> info);

            int passed = outcomes.FindAll(o => o.Status == "passed").Count;
            int flaky = outcomes.FindAll(o => o.Status == "flaky").Count;
            int failed = outcomes.Count - passed - flaky;
            Console.WriteLine(outcomes.Count + " rule(s): " + passed + " passed, " + flaky + " flaky, " + failed + " failed");

            string junit = args.Get("junit", null);
            if (junit != null) Junit.Write(junit, outcomes, info, total.Elapsed.TotalSeconds);
            if (args.Json)
                PrintJson(new Dictionary<string, object>
                {
                    ["passed"] = passed, ["flaky"] = flaky, ["failed"] = failed,
                    ["rules"] = outcomes.ConvertAll(o => (object)new Dictionary<string, object>
                    {
                        ["file"] = o.file, ["name"] = o.rule?.name, ["tc"] = o.rule?.tc, ["status"] = o.Status,
                        ["seconds"] = Math.Round(o.seconds, 3), ["code"] = o.failureCode, ["failures"] = o.failures,
                    }),
                });
            return failed == 0 ? 0 : 1;
        }

        /// <summary>규칙 파일들을 차례로 실행하고 파일마다 결과를 한 줄씩 찍는다. run과 bugs verify가 함께 쓴다.</summary>
        internal static List<Outcome> Execute(Args args, List<string> files, out Dictionary<string, object> info)
        {
            int retries = args.Int("retries", 1);
            App app = App.From(args, args.Int("port", AgentPort), () => Connect(args));
            BugStore bugs = BugStore.ForAutoReports(args);
            AgentClient client = null;
            info = null;
            Dictionary<string, object> hello = null;
            var outcomes = new List<Outcome>();
            try
            {
                app?.Launch();
                client = Connect(args); // 처음부터 게임에 닿지 못하면 여기서 끝난다(종료 코드 2)
                hello = client.Info;
                foreach (string file in files)
                {
                    var outcome = new Outcome { file = file };
                    var watch = Stopwatch.StartNew();
                    try
                    {
                        outcome.rule = RuleReader.Read(File.ReadAllText(file));
                    }
                    catch (FormatException e)
                    {
                        // 문법이 틀린 규칙은 한 줄도 실행하지 않고, 다시 돌리지도 않는다
                        outcome.Fail("syntax_error", e.Message);
                    }

                    if (outcome.rule != null)
                    {
                        outcome.RunWithRetries(retries, attempt =>
                        {
                            try
                            {
                                if (attempt > 0 && app != null)
                                {
                                    // 다시 돌릴 때는 게임을 새로 띄워 처음 상태에서 시작한다
                                    client?.Dispose();
                                    client = null;
                                    app.Stop(null, false);
                                    app.Launch();
                                }
                                client ??= Connect(args); // 앞에서 연결을 잃었으면 다시 붙는다
                                client.Exceptions.Clear();
                                RuleResult result = Attempt(outcome.rule, client);
                                // 아무 값도 읽지 않는 요청을 하나 보내, 그 앞에 게임이 보낸 이벤트를 모두 받아 낸다
                                client.Request("get", new Dictionary<string, object> { ["keys"] = new List<string>() });
                                if (client.Exceptions.Count == 0) return result;
                                outcome.evidence = client.Exceptions[0];
                                return new RuleResult
                                {
                                    code = "exception",
                                    message = string.Join("\n", client.Exceptions) + (result.passed ? "" : "\nalso: " + result),
                                };
                            }
                            catch (AgentLostException e)
                            {
                                // 크래시나 프리징: 이 연결은 끝났다. 띄운 게임이면 로그를 챙기고 다시 띄운다
                                client?.Dispose();
                                client = null;
                                string kind = e.Kind, note = e.Message;
                                if (app != null)
                                {
                                    if (kind == "freeze" && app.LooksCrashed())
                                    {
                                        kind = "crash";
                                        note = "the game crashed (it stopped responding first: " + note + ")";
                                    }
                                    note += Recover(app, kind, Path.GetFileNameWithoutExtension(file) + "." + kind + ".log", out outcome.logPath);
                                }
                                return new RuleResult { code = kind, message = note };
                            }
                            catch (Exception e) when (e is IOException || e is SocketException || e is FormatException)
                            {
                                client?.Dispose();
                                client = null;
                                return new RuleResult { code = "not_connected", message = e.Message };
                            }
                        });
                    }
                    outcome.seconds = watch.Elapsed.TotalSeconds;
                    Console.WriteLine(outcome.Report());
                    // 버그임이 분명한 실패(예외, 크래시, 프리징)는 버그 리포트로 바로 올린다
                    string registered = bugs?.RecordAuto(outcome, hello);
                    if (registered != null) Console.WriteLine("      " + registered);
                    outcomes.Add(outcome);
                }
            }
            finally
            {
                client?.Dispose();
                app?.Stop(null, false);
            }
            info = hello;
            return outcomes;
        }

        // 죽거나 멈춘 게임을 정리하고 다시 띄운다. 결과에 덧붙일 말을 돌려주고, 저장한 로그의 경로를 saved에 담는다
        static string Recover(App app, string kind, string logName, out string saved)
        {
            string note = "";
            saved = null;
            try
            {
                saved = app.Stop(logName, kind == "crash");
                if (app.ExitNote != null) note += "; " + app.ExitNote;
                if (saved != null) note += "; log saved to " + saved;
                app.Launch();
                return note + "; the game was restarted";
            }
            catch (Exception e) when (e is IOException || e is SocketException || e is UsageException)
            {
                return note + "; could not restart the game: " + e.Message;
            }
        }

        static RuleResult Attempt(Rule rule, AgentClient client)
        {
            RuleResult result = null;
            IEnumerator run = RuleRunner.Run(rule, client.Call, r => result = r);
            while (run.MoveNext()) { }
            return result;
        }

        // 파일은 그대로, 폴더는 그 아래의 .yaml을 모두(이름순)
        static List<string> RuleFiles(List<string> paths)
        {
            var files = new List<string>();
            foreach (string path in paths)
            {
                if (Directory.Exists(path))
                {
                    string[] found = Directory.GetFiles(path, "*.yaml", SearchOption.AllDirectories);
                    Array.Sort(found, StringComparer.Ordinal);
                    files.AddRange(found);
                }
                else if (File.Exists(path)) files.Add(path);
                else throw new UsageException("no such rule file or folder: " + path);
            }
            if (files.Count == 0) throw new UsageException("no rule files to run");
            return files;
        }

        // ---- check ----

        static int Check(Args args)
        {
            if (args.Paths.Count != 1) throw new UsageException("check needs exactly one rule file");
            Rule rule;
            try
            {
                rule = RuleReader.Read(File.ReadAllText(args.Paths[0]));
            }
            catch (FormatException e)
            {
                Console.WriteLine(e.Message);
                if (args.Json) PrintJson(new Dictionary<string, object> { ["syntaxError"] = e.Message });
                return 1;
            }

            using AgentClient client = Connect(args);
            List<RuleIssue> issues = null;
            IEnumerator check = RuleChecker.Check(rule, client.Call, found => issues = found);
            while (check.MoveNext()) { }
            foreach (RuleIssue issue in issues) Console.WriteLine(issue);
            Console.WriteLine(issues.Count == 0 ? "no issues on the current screen" : issues.Count + " issue(s) on the current screen");
            if (args.Json)
                PrintJson(new Dictionary<string, object>
                {
                    ["issues"] = issues.ConvertAll(i => (object)new Dictionary<string, object>
                    {
                        ["command"] = i.index, ["line"] = i.step.line, ["source"] = i.step.source,
                        ["kind"] = i.kind, ["count"] = i.count, ["message"] = i.message,
                    }),
                });
            return issues.Count == 0 ? 0 : 1;
        }

        // ---- dump, values: 규칙을 쓰기 전에 화면과 게임 값을 본다 ----

        static int Dump(Args args)
        {
            using AgentClient client = Connect(args);
            Dictionary<string, object> dump = Ok(client.Request("dump", null));
            var screen = (Dictionary<string, object>)dump["screen"];
            Console.WriteLine("screen " + screen["width"] + "x" + screen["height"]);
            foreach (object root in (List<object>)dump["roots"]) PrintNode((Dictionary<string, object>)root, 0);
            if (args.Json) PrintJson(dump);
            return 0;
        }

        // 요소 하나를 한 줄로: 깊이만큼 들여 쓰고 이름, 종류, id, 글자, 그리고 누를 수 있는지와 안 보이는지
        static void PrintNode(Dictionary<string, object> node, int depth)
        {
            var line = new StringBuilder(new string(' ', depth * 2)).Append(node["name"]).Append("  ").Append(node["type"]);
            if (node.TryGetValue("id", out object id)) line.Append("  id=").Append(id);
            if (node.TryGetValue("text", out object text)) line.Append("  ").Append(Json.Write(text));
            if (true.Equals(node["interactable"])) line.Append("  [tappable]");
            if (!true.Equals(node["visible"])) line.Append("  [hidden]");
            Console.WriteLine(line);
            foreach (object child in (List<object>)node["children"]) PrintNode((Dictionary<string, object>)child, depth + 1);
        }

        static int Values(Args args)
        {
            using AgentClient client = Connect(args);
            var values = (Dictionary<string, object>)Ok(client.Request("get", null))["values"];
            var keys = new List<string>(values.Keys);
            keys.Sort(StringComparer.Ordinal);
            if (keys.Count == 0) Console.WriteLine("the game registered no values (GameValues.Register)");
            foreach (string key in keys) Console.WriteLine(key + " = " + Json.Write(values[key]));
            if (args.Json) PrintJson(new Dictionary<string, object> { ["values"] = values });
            return 0;
        }

        // 예산(assertMemory)을 정하기 전에 지금 얼마나 쓰는지 본다. 단위는 규칙에 적는 것과 같은 MB다. --json은 바이트 그대로
        static int Memory(Args args)
        {
            using AgentClient client = Connect(args);
            Dictionary<string, object> memory = Ok(client.Request("memory", null));
            foreach (string layer in new[] { "managed", "native", "process" })
                Console.WriteLine(layer.PadRight(8) + (memory[layer] == null
                    ? " not available on this platform"
                    : RuleRunner.Megabytes(memory[layer]).ToString("0.0", CultureInfo.InvariantCulture).PadLeft(8) + " MB"));
            if (args.Json) PrintJson(memory);
            return 0;
        }

        static Dictionary<string, object> Ok(Dictionary<string, object> response)
        {
            if (true.Equals(response["ok"])) return (Dictionary<string, object>)response["result"];
            var error = (Dictionary<string, object>)response["error"];
            throw new IOException(error["code"] + ": " + error["message"]);
        }

        // ---- serve, devices ----

        static int Serve(Args args)
        {
            (string host, int port) = Target(args);
            App app = App.From(args, port, () => Connect(args));
            try
            {
                app?.Launch();
                int code = Relay.Run(args.Int("listen", RelayPort), host, port, args.Rest);
                if (app == null || !app.HasExited) return code;

                // 스크립트가 도는 동안 게임이 죽었다. 스크립트가 성공으로 끝났더라도 실패다
                string saved = app.Stop("script.crash.log", true);
                Console.Error.WriteLine("qaplay: the game exited while the script was running; " + app.ExitNote
                                        + (saved == null ? "" : "; log saved to " + saved));
                return code == 0 ? 1 : code;
            }
            finally
            {
                app?.Stop(null, false);
            }
        }

        static int Devices(Args args)
        {
            List<Adb.Device> devices = Adb.Parse(Adb.Run(Adb.Find(args.Get("adb", null)), "devices", "-l"));
            if (devices.Count == 0) Console.WriteLine("no devices");
            foreach (Adb.Device device in devices)
                Console.WriteLine(device.serial + "\t" + device.state + "\t" + device.model);
            return 0;
        }

        // ---- 연결 ----

        // 러너는 항상 자기 쪽 localhost 포트로 접속한다. Android는 그 포트를 기기의 에이전트 포트로 이어 둔다
        static (string host, int port) Target(Args args)
        {
            string host = args.Get("host", "127.0.0.1");
            int port = args.Int("port", AgentPort);
            string device = args.Get("device", null);
            if (device != null)
                Adb.Run(Adb.Find(args.Get("adb", null)), "-s", device, "forward", "tcp:" + port, "tcp:" + AgentPort);
            return (host, port);
        }

        internal static AgentClient Connect(Args args)
        {
            (string host, int port) = Target(args);
            try
            {
                return new AgentClient(host, port, args.Int("freeze-seconds", 10) * 1000);
            }
            catch (SocketException e)
            {
                throw new IOException("cannot reach the game at " + host + ":" + port
                                      + " (is it running, in Play mode or as a QA build?): " + e.Message);
            }
        }
    }

    sealed class UsageException : Exception
    {
        public UsageException(string message) : base(message) { }
    }

    /// <summary>실행 인자: 명령, 위치 인자, --이름 값, 그리고 -- 뒤의 나머지.</summary>
    sealed class Args
    {
        static readonly string[] Known =
        {
            "host", "port", "device", "adb", "retries", "junit", "listen", "app", "app-args", "package", "artifacts", "freeze-seconds",
            "bugs", "title", "description", "status", "severity", "priority", "to",
        };

        public readonly string Command;
        /// <summary>--json: 결과를 JSON으로 표준 출력에 낸다. 값이 없는 옵션이다.</summary>
        public readonly bool Json;
        public readonly List<string> Paths = new List<string>();
        public readonly List<string> Rest = new List<string>();
        readonly Dictionary<string, string> m_Options = new Dictionary<string, string>();

        public Args(string[] argv)
        {
            Command = argv.Length > 0 ? argv[0] : "";
            for (int i = 1; i < argv.Length; i++)
            {
                if (argv[i] == "--")
                {
                    for (i++; i < argv.Length; i++) Rest.Add(argv[i]);
                }
                else if (argv[i] == "--json") Json = true;
                else if (argv[i].StartsWith("--", StringComparison.Ordinal))
                {
                    string name = argv[i].Substring(2);
                    if (Array.IndexOf(Known, name) < 0) throw new UsageException("unknown option " + argv[i]);
                    if (i + 1 >= argv.Length) throw new UsageException(argv[i] + " needs a value");
                    m_Options[name] = argv[++i];
                }
                else Paths.Add(argv[i]);
            }
        }

        public string Get(string name, string fallback) => m_Options.TryGetValue(name, out string value) ? value : fallback;

        public int Int(string name, int fallback)
        {
            if (!m_Options.TryGetValue(name, out string value)) return fallback;
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number)) return number;
            throw new UsageException("--" + name + " must be a whole number, 0 or greater");
        }
    }

    /// <summary>규칙 파일 하나를 (다시 돌린 것까지) 실행한 결과.</summary>
    sealed class Outcome
    {
        public string file;
        public Rule rule;
        public double seconds;
        public bool passed;
        /// <summary>실패한 시도마다 한 줄. 통과했는데 이 목록이 비어 있지 않으면 불안정이다.</summary>
        public readonly List<string> failures = new List<string>();
        /// <summary>마지막 실패의 오류 코드.</summary>
        public string failureCode;
        /// <summary>exception으로 실패했을 때 첫 예외의 메시지와 스택 원문. 같은 버그인지 가리는 데 쓴다.</summary>
        public string evidence;
        /// <summary>crash나 freeze로 실패했을 때 저장해 둔 게임 로그의 경로. 없으면 null.</summary>
        public string logPath;

        public string Status => !passed ? "failed" : failures.Count > 0 ? "flaky" : "passed";

        public void Fail(string code, string text)
        {
            failureCode = code;
            failures.Add(text);
        }

        // 통과할 때까지 최대 1 + retries번 실행한다
        // 예외, 크래시, 프리징은 가끔만 나더라도 버그이므로 다시 돌려서 덮지 않는다
        public void RunWithRetries(int retries, Func<int, RuleResult> attempt)
        {
            for (int n = 0; n <= retries && !passed; n++)
            {
                RuleResult result = attempt(n);
                if (result.passed)
                {
                    passed = true;
                    break;
                }
                Fail(result.code, result.step == null ? result.code + ": " + result.message : result.ToString());
                if (result.code == "exception" || result.code == "crash" || result.code == "freeze") break;
            }
        }

        public string Report()
        {
            var sb = new StringBuilder();
            sb.Append(Status == "passed" ? "PASS  " : Status == "flaky" ? "FLAKY " : "FAIL  ").Append(file)
                .Append(" (").Append(seconds.ToString("0.0", CultureInfo.InvariantCulture)).Append("s)");
            for (int n = 0; n < failures.Count; n++)
                sb.Append("\n      attempt ").Append(n + 1).Append(": ").Append(failures[n]);
            return sb.ToString();
        }
    }

    /// <summary>CI가 읽는 JUnit XML. 규칙 파일 하나가 testcase 하나다.</summary>
    static class Junit
    {
        public static void Write(string path, List<Outcome> outcomes, Dictionary<string, object> info, double seconds)
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(folder);
            using XmlWriter xml = XmlWriter.Create(path, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) });
            xml.WriteStartElement("testsuites");
            xml.WriteStartElement("testsuite");
            xml.WriteAttributeString("name", "qaplay");
            xml.WriteAttributeString("tests", outcomes.Count.ToString(CultureInfo.InvariantCulture));
            xml.WriteAttributeString("failures", outcomes.FindAll(o => !o.passed).Count.ToString(CultureInfo.InvariantCulture));
            xml.WriteAttributeString("time", Time(seconds));

            // 어떤 빌드와 기기에서 돌렸는지
            xml.WriteStartElement("properties");
            var app = info["app"] as Dictionary<string, object>;
            var screen = info["screen"] as Dictionary<string, object>;
            Property(xml, "app.id", app?["id"]);
            Property(xml, "app.version", app?["version"]);
            Property(xml, "platform", info["platform"]);
            Property(xml, "unity", info["unity"]);
            Property(xml, "screen", screen == null ? null : screen["width"] + "x" + screen["height"]);
            xml.WriteEndElement();

            foreach (Outcome outcome in outcomes)
            {
                xml.WriteStartElement("testcase");
                xml.WriteAttributeString("classname", outcome.rule?.tc ?? "qaplay");
                xml.WriteAttributeString("name", outcome.rule?.name ?? Path.GetFileNameWithoutExtension(outcome.file));
                xml.WriteAttributeString("file", outcome.file);
                xml.WriteAttributeString("time", Time(outcome.seconds));
                if (!outcome.passed)
                {
                    xml.WriteStartElement("failure");
                    xml.WriteAttributeString("type", outcome.failureCode);
                    xml.WriteAttributeString("message", outcome.failures[outcome.failures.Count - 1]);
                    xml.WriteString(string.Join("\n", outcome.failures));
                    xml.WriteEndElement();
                }
                else
                {
                    // 다시 돌려 통과한 테스트: 통과로 세되, 실패했던 시도를 남긴다(Surefire의 표기)
                    foreach (string failure in outcome.failures)
                    {
                        xml.WriteStartElement("flakyFailure");
                        xml.WriteAttributeString("type", outcome.failureCode);
                        xml.WriteAttributeString("message", failure);
                        xml.WriteEndElement();
                    }
                }
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        static void Property(XmlWriter xml, string name, object value)
        {
            if (value == null) return;
            xml.WriteStartElement("property");
            xml.WriteAttributeString("name", name);
            xml.WriteAttributeString("value", Convert.ToString(value, CultureInfo.InvariantCulture));
            xml.WriteEndElement();
        }

        static string Time(double seconds) => seconds.ToString("0.000", CultureInfo.InvariantCulture);
    }
}
