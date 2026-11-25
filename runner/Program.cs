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
            "  qaplay serve [--listen PORT] [-- command...]\n" +
            "                                       relay for Python scripts; with a command, run it and exit with its code\n" +
            "  qaplay devices                       list Android devices\n" +
            "connection options (run, check, serve):\n" +
            "  [--host HOST] [--port PORT]          where the game listens (default 127.0.0.1:27275)\n" +
            "  [--device SERIAL] [--adb PATH]       Android: forward the port to this device first\n" +
            "exit code 2 = bad usage or the game could not be reached";

        static int Main(string[] argv)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            try
            {
                var args = new Args(argv);
                switch (args.Command)
                {
                    case "run": return Run(args);
                    case "check": return Check(args);
                    case "serve": return Serve(args);
                    case "devices": return Devices(args);
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
            List<string> files = RuleFiles(args.Paths);
            int retries = args.Int("retries", 1);
            AgentClient client = Connect(args);
            Dictionary<string, object> info = client.Info;
            var outcomes = new List<Outcome>();
            var total = Stopwatch.StartNew();
            try
            {
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
                        outcome.RunWithRetries(retries, () =>
                        {
                            try
                            {
                                client ??= Connect(args); // 앞 시도에서 연결이 끊겼으면 다시 붙는다
                                return Attempt(outcome.rule, client);
                            }
                            catch (Exception e) when (e is IOException || e is SocketException || e is FormatException)
                            {
                                client?.Dispose();
                                client = null;
                                return new RuleResult { code = "connection_lost", message = e.Message };
                            }
                        });
                    }
                    outcome.seconds = watch.Elapsed.TotalSeconds;
                    Console.WriteLine(outcome.Report());
                    outcomes.Add(outcome);
                }
            }
            finally
            {
                client?.Dispose();
            }

            int passed = outcomes.FindAll(o => o.Status == "passed").Count;
            int flaky = outcomes.FindAll(o => o.Status == "flaky").Count;
            int failed = outcomes.Count - passed - flaky;
            Console.WriteLine(outcomes.Count + " rule(s): " + passed + " passed, " + flaky + " flaky, " + failed + " failed");

            string junit = args.Get("junit", null);
            if (junit != null) Junit.Write(junit, outcomes, info, total.Elapsed.TotalSeconds);
            return failed == 0 ? 0 : 1;
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
                return 1;
            }

            using AgentClient client = Connect(args);
            List<RuleIssue> issues = null;
            IEnumerator check = RuleChecker.Check(rule, client.Call, found => issues = found);
            while (check.MoveNext()) { }
            foreach (RuleIssue issue in issues) Console.WriteLine(issue);
            Console.WriteLine(issues.Count == 0 ? "no issues on the current screen" : issues.Count + " issue(s) on the current screen");
            return issues.Count == 0 ? 0 : 1;
        }

        // ---- serve, devices ----

        static int Serve(Args args)
        {
            (string host, int port) = Target(args);
            return Relay.Run(args.Int("listen", RelayPort), host, port, args.Rest);
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

        static AgentClient Connect(Args args)
        {
            (string host, int port) = Target(args);
            try
            {
                return new AgentClient(host, port);
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
        static readonly string[] Known = { "host", "port", "device", "adb", "retries", "junit", "listen" };

        public readonly string Command;
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

        public string Status => !passed ? "failed" : failures.Count > 0 ? "flaky" : "passed";

        public void Fail(string code, string text)
        {
            failureCode = code;
            failures.Add(text);
        }

        // 통과할 때까지 최대 1 + retries번 실행한다
        // ponytail: 다시 돌리기 전에 게임을 처음 상태로 되돌리지 않는다. 앞 시도가 남긴 상태에서 이어서 돈다.
        // 앱 재시작과 상태 초기화는 4단계(크래시 복구)에서 넣는다
        public void RunWithRetries(int retries, Func<RuleResult> attempt)
        {
            for (int n = 0; n <= retries && !passed; n++)
            {
                RuleResult result = attempt();
                if (result.passed) passed = true;
                else Fail(result.code, result.step == null ? result.code + ": " + result.message : result.ToString());
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
