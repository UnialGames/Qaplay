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
            "  qaplay run [<rule.yaml | folder>...] run rules (none given: the rules folders of the settings);\n" +
            "                                       exit code 0 = all passed (flaky counts as passed), 1 = failed\n" +
            "      [--retries N]   rerun a failed rule up to N times (default 1); passing on a rerun is reported as flaky\n" +
            "      [--junit FILE]  also write the results as JUnit XML\n" +
            "      the results also go to the test case sheet, when the settings connect one (qaplay tc init)\n" +
            "      [--reports DIR] keep this run (results, numbers, failed screens, game log) in DIR (default qaplay-reports; \"off\" to disable)\n" +
            "  qaplay reports ...                   the runs kept: list, show, keep, unkeep, delete, expired, clean, retention (run: qaplay reports)\n" +
            "  qaplay tc ...                        the test case sheet: init, list, show, sync, accept (run: qaplay tc)\n" +
            "  qaplay settings                      show the values in use, where each came from, and what is wrong (exit code 1 if anything)\n" +
            "  qaplay modules                       the modules next to the runner (modules/NAME) and what each adds (Excel file and\n" +
            "                                       Google Sheets sheets); exit code 1 if one cannot be loaded\n" +
            "  qaplay dashboard [--listen PORT]     show the runs kept in --reports DIR in a browser; stays up until Ctrl+C (default port 27280)\n" +
            "      [--listen ADDRESS:PORT]          accept other PCs too, for example 0.0.0.0:27280 (there is no login)\n" +
            "  qaplay check <rule.yaml>             check the selectors against the current screen without running\n" +
            "  qaplay dump                          list the elements on the current screen (what a rule can tap and assert)\n" +
            "  qaplay values                        list the game values a rule can read with assertValue\n" +
            "  qaplay layout                        find layout problems on the current screen (text that does not fit, elements that overlap); exit code 1 if any\n" +
            "      [--screenshot FILE]  also save the screen as a PNG, to see what the issues look like\n" +
            "  qaplay exceptions ...                layout issues that are intended: list, add, set, delete (run: qaplay exceptions)\n" +
            "      run and layout skip the issues listed in --exceptions FILE (default qaplay-layout-exceptions.json)\n" +
            "  qaplay mocks                         list the mocks the game registered and the scenarios a rule can pick with mock\n" +
            "  qaplay memory                        show the memory the game uses now (what assertMemory compares, in MB)\n" +
            "  qaplay frames [--seconds N]          measure frame times for N seconds (default 5): what assertFrameTime compares\n" +
            "  qaplay device [--seconds N]          read what only the OS knows, over N seconds (default 5): CPU, and on Android PSS,\n" +
            "                                       thermal status and battery; needs --device, or --app for a PC build (CPU only)\n" +
            "      [--json]        run, check, dump, values, layout, mocks, memory, frames, device, devices, settings, modules:\n" +
            "                      print the result as JSON on stdout;\n" +
            "                      messages go to stderr\n" +
            "  qaplay serve [--listen PORT] [-- command...]\n" +
            "                                       relay for Python scripts; with a command, run it and exit with its code\n" +
            "  qaplay devices                       list Android devices\n" +
            "  qaplay bugs ...                      bug reports: pull, report, list, show, set, delete, promote, verify (run: qaplay bugs)\n" +
            "      run registers exceptions, crashes and freezes as bugs in --bugs DIR (default qaplay-bugs; \"off\" to disable),\n" +
            "      and sends them to the issue tracker of the settings (issues: Jira with the Jira module)\n" +
            "settings: every command reads qaplay.json in the current folder (or --settings FILE): the target, the rules folders,\n" +
            "  retries, ports and folders, and the sheet. In Unity they are set in Project Settings > qaplay.\n" +
            "  A value given on the command line wins over the file.\n" +
            "connection options (run, check, dump, values, serve):\n" +
            "  [--host HOST] [--port PORT]          where the game listens (default 127.0.0.1:27275)\n" +
            "  [--device SERIAL] [--adb PATH]       Android: forward the port to this device first (settings: the one connected)\n" +
            "  [--freeze-seconds N]                 a game whose frames stop for N seconds is frozen (default 10)\n" +
            "let the runner start the game, so it can restart it after a crash or a freeze (run, serve):\n" +
            "  [--app GAME.EXE] [--app-args \"...\"]  a PC build\n" +
            "  [--package NAME]                     an Android app on --device\n" +
            "  [--artifacts DIR]                    where game logs are saved (default qaplay-artifacts)\n" +
            "  [--launch-seconds N]                 how long to wait for the game to answer after starting it (default 60)\n" +
            "a C# exception, a crash or a freeze fails the rule and is never rerun\n" +
            "exit code 2 = bad usage or the game could not be reached";

        // --json의 결과가 나가는 곳. Console.Out은 --json일 때 표준 오류로 바뀐다
        static TextWriter s_Stdout;

        internal static void PrintJson(object data) => s_Stdout.WriteLine(Json.Write(data));

        static int Main(string[] argv)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            s_Stdout = Console.Out;
            try
            {
                var args = new Args(argv);
                // 자체 검사는 실행한 폴더의 설정에 흔들리지 않는다
                if (args.Command == "selftest") return SelfTest.Run();

                // 조립 지점: 프로젝트 설정, 설치된 모듈, 쓸 수 있는 시트의 종류를 여기서 한 번 만들어 필요한 명령에 넘긴다
                ProjectSettings settings = ProjectSettings.Load(args);
                bool creates = args.Command == "tc" && args.Paths.Count > 0 && args.Paths[0] == "init";
                if (args.Given("settings") && !settings.Exists && !creates) throw new UsageException("no such settings file: " + settings.File);
                settings.ApplyTo(args);
                Modules modules = Modules.Load();
                SheetProviders sheets = SheetProviders.Installed(modules);
                IssueTrackers trackers = IssueTrackers.Installed(modules);
                if (args.Json)
                {
                    string[] json = { "run", "check", "dump", "values", "tc", "reports", "layout", "exceptions", "mocks", "memory", "frames", "device", "devices", "settings", "modules", "bugs" };
                    if (Array.IndexOf(json, args.Command) < 0)
                        throw new UsageException("--json is supported by " + string.Join(", ", json, 0, json.Length - 1) + " and " + json[json.Length - 1]);
                    // 기계가 읽는 결과만 표준 출력으로 내보낸다. 사람이 읽는 줄은 모두 표준 오류로 간다
                    Console.SetOut(Console.Error);
                }
                switch (args.Command)
                {
                    case "run": return Run(args, settings, sheets, trackers);
                    case "settings": return ProjectSettings.Command(args, settings);
                    case "check": return Check(args);
                    case "dump": return Dump(args);
                    case "values": return Values(args);
                    case "tc": return TcCommands.Run(args, settings, sheets, modules);
                    case "modules": return Modules.Command(args, modules, sheets);
                    case "reports": return ReportCommands.Run(args, settings);
                    case "dashboard": return Dashboard.Run(args, settings);
                    case "layout": return Layout(args);
                    case "exceptions": return ExceptionCommands.Run(args);
                    case "mocks": return Mocks(args);
                    case "memory": return Memory(args);
                    case "frames": return FrameTimes(args);
                    case "device": return Device(args);
                    case "serve": return Serve(args);
                    case "devices": return Devices(args);
                    case "bugs": return BugCommands.Run(args, settings, trackers, modules);
                    default:
                        Console.Error.WriteLine(Usage);
                        return 2;
                }
            }
            catch (Exception e) when (e is UsageException || e is IOException || e is SocketException || e is UnauthorizedAccessException)
            {
                Console.Error.WriteLine("qaplay: " + e.Message);
                return 2;
            }
        }

        // ---- run ----

        static int Run(Args args, ProjectSettings settings, SheetProviders sheets, IssueTrackers trackers)
        {
            var total = Stopwatch.StartNew();
            // 시트 설정이 틀렸으면 실행하기 전에 알린다
            SheetConfig sheet = SheetConfig.From(settings, sheets);
            // 규칙을 주지 않으면 설정의 규칙 폴더를 돌린다
            List<string> paths = args.Paths.Count > 0 ? args.Paths : settings.Rules;
            if (paths.Count == 0) throw new UsageException("nothing to run: give rule files or folders, or set the rules folders in the settings (Unity: Project Settings > qaplay)");
            // 결과를 남길 곳들(리포트, 시트, JUnit, CI). 설정이 틀렸으면 실행하기 전에 여기서 알린다:
            // 실행이 끝난 뒤에야 알리면 그 실행의 기록이 남지 않는다
            List<IRunOutput> outputs = RunOutputs.For(args, settings, sheet == null ? null : sheets.Sheet(sheet), out RunEvidence evidence);
            var run = new RunRecord { started = DateTime.UtcNow, evidence = evidence };
            run.outcomes = Execute(args, RuleFiles(paths), out run.hello, evidence);

            List<Outcome> outcomes = run.outcomes;
            int passed = outcomes.FindAll(o => o.Status == "passed").Count;
            int flaky = outcomes.FindAll(o => o.Status == "flaky").Count;
            int failed = outcomes.Count - passed - flaky;
            Console.WriteLine(outcomes.Count + " rule(s): " + passed + " passed, " + flaky + " flaky, " + failed + " failed");

            run.build = BuildOf(run.hello);
            run.device = DeviceOf(run.hello, args);
            run.seconds = total.Elapsed.TotalSeconds;
            RunOutputs.WriteAll(outputs, run);
            // 이번에 올린 버그를 이슈 관리 도구에도 보낸다. 보내지 못해도 실행 결과는 그대로다
            BugStore bugs = BugStore.ForAutoReports(args);
            if (bugs != null && settings.Issues != null && outcomes.Exists(o => o.bugId != null)) BugCommands.Sync(bugs, settings, trackers);
            if (args.Json)
                PrintJson(new Dictionary<string, object>
                {
                    ["passed"] = passed, ["flaky"] = flaky, ["failed"] = failed, ["report"] = run.reportFolder,
                    ["rules"] = outcomes.ConvertAll(o => (object)o.Data()),
                });
            return failed == 0 ? 0 : 1;
        }

        /// <summary>규칙 파일들을 차례로 실행하고 파일마다 결과를 한 줄씩 찍는다. run과 bugs verify가 함께 쓴다.</summary>
        /// <param name="evidence">있으면 실패한 화면을 찍어 두고, 러너가 띄운 게임의 로그를 끝날 때 남긴다(실행 리포트에 들어간다).</param>
        internal static List<Outcome> Execute(Args args, List<string> files, out Dictionary<string, object> info, RunEvidence evidence = null)
        {
            int retries = args.Int("retries", 1);
            // 게임이 도는 곳(에디터, PC 빌드, Android). 띄운 게임의 에이전트가 답하는지는 그 대상을 거쳐 붙어 본다
            GameTarget target = null;
            target = GameTarget.From(args, args.Int("port", AgentPort), () => Connect(args, target));
            BugStore bugs = BugStore.ForAutoReports(args);
            AgentClient client = null;
            info = null;
            Dictionary<string, object> hello = null;
            var outcomes = new List<Outcome>();
            try
            {
                target.Launch();
                client = Connect(args, target); // 처음부터 게임에 닿지 못하면 여기서 끝난다(종료 코드 2)
                hello = client.Info;
                var device = new DeviceMeter(target, AppId(hello));
                LayoutExceptions exceptions = LayoutExceptions.Load(args);
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
                                if (attempt > 0 && target.CanLaunch)
                                {
                                    // 다시 돌릴 때는 게임을 새로 띄워 처음 상태에서 시작한다
                                    client?.Dispose();
                                    client = null;
                                    target.Stop(null, false);
                                    target.Launch();
                                }
                                client ??= Connect(args, target); // 앞에서 연결을 잃었으면 다시 붙는다
                                client.Exceptions.Clear();
                                RuleResult result = Attempt(outcome.rule, client, device, exceptions, out outcome.ignored);
                                if (result.memory != null) outcome.SaveMemory(result, args.Get("artifacts", "qaplay-artifacts"));
                                outcome.frames = result.frames;
                                outcome.device = result.device;
                                // 아무 값도 읽지 않는 요청을 하나 보내, 그 앞에 게임이 보낸 이벤트를 모두 받아 낸다
                                client.Request("get", new Dictionary<string, object> { ["keys"] = new List<string>() });
                                if (client.Exceptions.Count > 0)
                                {
                                    outcome.evidence = client.Exceptions[0];
                                    result = new RuleResult
                                    {
                                        code = "exception",
                                        message = string.Join("\n", client.Exceptions) + (result.passed ? "" : "\nalso: " + result),
                                    };
                                }
                                // 실패한 그 화면을 남긴다. 다시 돌려 통과해도(불안정) 실패했던 시도의 화면이 남는다
                                if (!result.passed && evidence != null) outcome.screenshot = Screenshot(client) ?? outcome.screenshot;
                                return result;
                            }
                            catch (AgentLostException e)
                            {
                                // 크래시나 프리징: 이 연결은 끝났다. 띄운 게임이면 로그를 챙기고 다시 띄운다
                                client?.Dispose();
                                client = null;
                                string kind = e.Kind, note = e.Message;
                                if (target.CanLaunch)
                                {
                                    if (kind == "freeze" && target.LooksCrashed())
                                    {
                                        kind = "crash";
                                        note = "the game crashed (it stopped responding first: " + note + ")";
                                    }
                                    note += Recover(target, kind, Path.GetFileNameWithoutExtension(file) + "." + kind + ".log", out outcome.logPath);
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
                string log = target.Stop(evidence == null ? null : "game.log", false);
                if (evidence != null) evidence.gameLog = log;
            }
            info = hello;
            return outcomes;
        }

        // 지금 화면을 PNG로. 그리지 않는 게임(배치 모드)이나 이 명령을 모르는 에이전트면 null
        static byte[] Screenshot(AgentClient client)
        {
            Dictionary<string, object> response = client.Request("screenshot", null);
            if (!true.Equals(response["ok"])) return null;
            return ((Dictionary<string, object>)response["result"])["png"] is string png ? Convert.FromBase64String(png) : null;
        }

        // 죽거나 멈춘 게임을 정리하고 다시 띄운다. 결과에 덧붙일 말을 돌려주고, 저장한 로그의 경로를 saved에 담는다
        static string Recover(GameTarget target, string kind, string logName, out string saved)
        {
            string note = "";
            saved = null;
            try
            {
                saved = target.Stop(logName, kind == "crash");
                if (target.ExitNote != null) note += "; " + target.ExitNote;
                if (saved != null) note += "; log saved to " + saved;
                target.Launch();
                return note + "; the game was restarted";
            }
            catch (Exception e) when (e is IOException || e is SocketException || e is UsageException)
            {
                return note + "; could not restart the game: " + e.Message;
            }
        }

        // 어떤 빌드였는지: 앱 식별자와 버전
        static string BuildOf(Dictionary<string, object> hello)
        {
            var app = hello["app"] as Dictionary<string, object>;
            return (app?["id"] + " " + app?["version"]).Trim();
        }

        // 어떤 기기였는지: 플랫폼과 화면 크기, Android면 기기의 시리얼
        static string DeviceOf(Dictionary<string, object> hello, Args args)
        {
            var screen = hello["screen"] as Dictionary<string, object>;
            string device = args.Get("device", null);
            return hello["platform"] + (screen == null ? "" : " " + screen["width"] + "x" + screen["height"]) + (device == null ? "" : " " + device);
        }

        static string AppId(Dictionary<string, object> hello) => (hello["app"] as Dictionary<string, object>)?["id"] as string;

        static RuleResult Attempt(Rule rule, AgentClient client, DeviceMeter device, LayoutExceptions exceptions, out int ignored)
        {
            RuleResult result = null;
            int skipped = 0;
            AgentCall call = (cmd, a, onResponse) =>
            {
                // device는 에이전트의 명령이 아니다. OS에서 읽어야 하는 값이라 러너가 답한다
                if (cmd == "device") return device.Call(a, onResponse);
                // 배치 검사의 결과에서 예외로 등록된 문제를 뺀 뒤에 판정하게 한다
                if (cmd == "layout")
                    return client.Call(cmd, a, response =>
                    {
                        if (true.Equals(response["ok"])) skipped += exceptions.Filter((Dictionary<string, object>)response["result"]);
                        onResponse(response);
                    });
                return client.Call(cmd, a, onResponse);
            };
            IEnumerator run = RuleRunner.Run(rule, call, r => result = r);
            while (run.MoveNext()) { }
            ignored = skipped;
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

        // 지금 화면의 배치 문제(글자 넘침, 요소 겹침)를 찾는다. 규칙의 assertLayout이 보는 것과 같다. 아무것도 누르지 않는다
        static int Layout(Args args)
        {
            LayoutExceptions exceptions = LayoutExceptions.Load(args);
            using AgentClient client = Connect(args);
            Dictionary<string, object> result = Ok(client.Request("layout", null));
            int ignored = exceptions.Filter(result);
            var issues = (List<object>)result["issues"];
            // 번호는 qaplay exceptions add에 그대로 쓴다
            for (int n = 0; n < issues.Count; n++)
            {
                var issue = (Dictionary<string, object>)issues[n];
                // 사람이 읽는 한 줄. 설정 화면이 그대로 보여 준다
                issue["description"] = RuleRunner.DescribeIssue(issue);
                Console.WriteLine((n + 1) + ". " + issue["kind"] + ": " + issue["description"]);
            }
            Console.WriteLine(issues.Count == 0 ? "no layout issues on the current screen" : issues.Count + " layout issue(s) on the current screen");
            if (ignored > 0) Console.WriteLine(ignored + " more ignored by the exceptions in " + exceptions.File);
            result["ignored"] = ignored;

            // 문제가 실제로 어떻게 보이는지 확인할 화면. 요소의 rect에 화면 크기를 곱하면 그림에서의 자리다
            string shot = args.Get("screenshot", null);
            if (shot != null)
            {
                Dictionary<string, object> screen = Ok(client.Request("screenshot", null));
                if (screen["png"] == null) throw new IOException("the game could not capture its screen (it does not draw in batch mode)");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(shot)));
                File.WriteAllBytes(shot, Convert.FromBase64String((string)screen["png"]));
                Console.WriteLine("screen saved to " + Path.GetFullPath(shot));
                result["screenshot"] = Path.GetFullPath(shot);
                result["screen"] = screen["screen"];
            }
            if (args.Json) PrintJson(result);
            return issues.Count == 0 ? 0 : 1;
        }

        // 규칙의 mock 명령으로 고를 수 있는 것: 게임이 등록한 Mock과 응답 시나리오. 맨 앞이 기본값이고 지금 고른 것은 = 뒤에 나온다
        static int Mocks(Args args)
        {
            using AgentClient client = Connect(args);
            var mocks = (Dictionary<string, object>)Ok(client.Request("mock", null))["mocks"];
            var names = new List<string>(mocks.Keys);
            names.Sort(StringComparer.Ordinal);
            if (names.Count == 0) Console.WriteLine("the game registered no mocks (Mocks.Register)");
            foreach (string name in names)
            {
                var mock = (Dictionary<string, object>)mocks[name];
                Console.WriteLine(name + " = " + mock["scenario"] + "  (" + string.Join(", ", (List<object>)mock["scenarios"]) + ")");
            }
            if (args.Json) PrintJson(new Dictionary<string, object> { ["mocks"] = mocks });
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

        // 예산(assertFrameTime)을 정하기 전에 지금 화면의 프레임 시간을 본다: 재기를 시작하고, 기다렸다가, 읽는다
        static int FrameTimes(Args args)
        {
            int seconds = args.Int("seconds", 5);
            using AgentClient client = Connect(args);
            Ok(client.Request("frames", null));
            System.Threading.Thread.Sleep(seconds * 1000);
            Dictionary<string, object> frames = Ok(client.Request("frames", null));
            Console.WriteLine(Outcome.FrameSummary(frames));
            if (args.Json) PrintJson(frames);
            return 0;
        }

        // 예산(assertDevice)을 정하기 전에 OS에서 읽은 기기 지표를 본다. --app을 주면 그 빌드를 띄워서 잰다
        static int Device(Args args)
        {
            int seconds = args.Int("seconds", 5);
            GameTarget target = null;
            target = GameTarget.From(args, args.Int("port", AgentPort), () => Connect(args, target));
            try
            {
                target.Launch();
                using AgentClient client = Connect(args, target);
                var meter = new DeviceMeter(target, AppId(client.Info));
                meter.Take(true);
                System.Threading.Thread.Sleep(seconds * 1000);
                Dictionary<string, object> device = meter.Take(false);
                Console.WriteLine(DeviceMeter.Summary(device));
                if (args.Json) PrintJson(device);
                return 0;
            }
            finally
            {
                target.Stop(null, false);
            }
        }

        internal static Dictionary<string, object> Ok(Dictionary<string, object> response)
        {
            if (true.Equals(response["ok"])) return (Dictionary<string, object>)response["result"];
            var error = (Dictionary<string, object>)response["error"];
            throw new IOException(error["code"] + ": " + error["message"]);
        }

        // ---- serve, devices ----

        static int Serve(Args args)
        {
            string host = args.Get("host", "127.0.0.1");
            int port = args.Int("port", AgentPort);
            GameTarget target = null;
            target = GameTarget.From(args, port, () => Connect(args, target));
            try
            {
                target.Prepare(port);
                target.Launch();
                int code = Relay.Run(args.Int("listen", RelayPort), host, port, args.Rest);
                if (!target.HasExited) return code;

                // 스크립트가 도는 동안 게임이 죽었다. 스크립트가 성공으로 끝났더라도 실패다
                string saved = target.Stop("script.crash.log", true);
                Console.Error.WriteLine("qaplay: the game exited while the script was running; " + target.ExitNote
                                        + (saved == null ? "" : "; log saved to " + saved));
                return code == 0 ? 1 : code;
            }
            finally
            {
                target.Stop(null, false);
            }
        }

        static int Devices(Args args)
        {
            List<Adb.Device> devices = Adb.Parse(Adb.Run(Adb.Find(args.Get("adb", null)), "devices", "-l"));
            if (devices.Count == 0) Console.WriteLine("no devices");
            foreach (Adb.Device device in devices)
                Console.WriteLine(device.serial + "\t" + device.state + "\t" + device.model);
            // 설정 화면이 기기 목록을 보여 줄 때 쓴다
            if (args.Json)
                PrintJson(new Dictionary<string, object>
                {
                    ["devices"] = devices.ConvertAll(d => (object)new Dictionary<string, object> { ["serial"] = d.serial, ["state"] = d.state, ["model"] = d.model }),
                });
            return 0;
        }

        // ---- 연결 ----

        /// <summary>게임에 붙는다(띄우지는 않는다). 대상은 명령의 값과 프로젝트 설정에서 고른다.</summary>
        internal static AgentClient Connect(Args args) => Connect(args, GameTarget.From(args, args.Int("port", AgentPort), null));

        // 러너는 항상 자기 쪽 포트로 접속한다. 그 포트가 게임에 닿게 하는 것은 대상이 한다(Android: 기기의 에이전트 포트로 잇는다)
        internal static AgentClient Connect(Args args, GameTarget target)
        {
            string host = args.Get("host", "127.0.0.1");
            int port = args.Int("port", AgentPort);
            target.Prepare(port);
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
            "host", "port", "device", "adb", "retries", "junit", "listen", "app", "app-args", "package", "artifacts", "freeze-seconds", "seconds",
            "bugs", "title", "description", "status", "severity", "priority", "to", "exceptions", "reason", "screenshot", "sheet", "reports",
            "spreadsheet", "credentials", "tab", "lang", "rules", "tc-column", "content-column", "expected-column", "type", "file",
            "settings", "launch-seconds", "issue-kind", "issue-path", "issue-other-path", "site", "email", "project", "issue-type",
        };

        public readonly string Command;
        /// <summary>--json: 결과를 JSON으로 표준 출력에 낸다. 값이 없는 옵션이다.</summary>
        public readonly bool Json;
        /// <summary>--replace: 이미 있는 것을 바꾼다(tc init: 이어진 시트를 다른 시트로). 값이 없는 옵션이다.</summary>
        public readonly bool Replace;
        public readonly List<string> Paths = new List<string>();
        public readonly List<string> Rest = new List<string>();
        readonly Dictionary<string, string> m_Options = new Dictionary<string, string>();
        // 명령줄에 주지 않았을 때 쓰는 값(프로젝트 설정 파일에서 온다). 함수는 처음 쓸 때 한 번만 부른다
        readonly Dictionary<string, Func<string>> m_Fallbacks = new Dictionary<string, Func<string>>();
        readonly Dictionary<string, string> m_Resolved = new Dictionary<string, string>();

        /// <summary>명령줄에 그 옵션을 줬는지.</summary>
        public bool Given(string name) => m_Options.ContainsKey(name);

        /// <summary>명령줄에 주지 않았을 때 쓸 값을 정한다(프로젝트 설정).</summary>
        public void Fallback(string name, string value) => m_Fallbacks[name] = () => value;

        /// <summary>명령줄에 주지 않았을 때, 처음 쓰일 때 구해서 쓸 값(연결된 기기 찾기처럼 느린 것).</summary>
        public void Fallback(string name, Func<string> value) => m_Fallbacks[name] = value;

        string Lookup(string name)
        {
            if (m_Options.TryGetValue(name, out string value)) return value;
            if (m_Resolved.TryGetValue(name, out value)) return value;
            if (!m_Fallbacks.TryGetValue(name, out Func<string> find)) return null;
            return m_Resolved[name] = find();
        }

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
                else if (argv[i] == "--replace") Replace = true;
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

        public string Get(string name, string fallback) => Lookup(name) ?? fallback;

        public int Int(string name, int fallback)
        {
            string value = Lookup(name);
            if (value == null) return fallback;
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
        /// <summary>이 실패가 버그 리포트로 올라갔으면 그 번호(예: 0003). 아니면 null.</summary>
        public string bugId;
        /// <summary>leakCheck가 있는 규칙의 마지막 시도: 계층별 증가 추세(되풀이 한 번에 MB)와, 잰 값을 남긴 파일. 없으면 null.</summary>
        public Dictionary<string, object> growth;
        public string memoryLog;
        /// <summary>마지막 시도의 마지막 assertFrameTime이 받은 프레임 시간 요약. 없으면 null.</summary>
        public Dictionary<string, object> frames;
        /// <summary>마지막 시도의 마지막 assertDevice가 받은 기기 지표. 없으면 null.</summary>
        public Dictionary<string, object> device;
        /// <summary>마지막 시도에서 예외 목록 덕분에 넘어간 배치 문제의 수.</summary>
        public int ignored;
        /// <summary>마지막으로 실패한 시도의 화면(PNG). 실행 리포트를 남길 때만 찍는다. 없으면 null.</summary>
        public byte[] screenshot;

        /// <summary>--json과 실행 리포트에 나가는 모습.</summary>
        public Dictionary<string, object> Data() => new Dictionary<string, object>
        {
            ["file"] = file, ["name"] = rule?.name, ["tc"] = rule?.tc, ["status"] = Status,
            ["seconds"] = Math.Round(seconds, 3), ["code"] = failureCode, ["failures"] = failures, ["bug"] = bugId,
            ["growth"] = growth, ["memoryLog"] = memoryLog, ["frames"] = frames, ["device"] = device, ["ignored"] = ignored,
        };

        /// <summary>frames 명령의 결과를 한 줄로.</summary>
        public static string FrameSummary(Dictionary<string, object> frames) =>
            Convert.ToInt32(frames["count"]) == 0
                ? "no frames were measured"
                : "average " + Ms(frames["averageMs"]) + ", p99 " + Ms(frames["p99Ms"]) + ", longest " + Ms(frames["maxMs"]) + ", "
                  + frames["hitches"] + " hitch(es) over " + frames["hitchMs"] + " ms, " + frames["count"] + " frames in "
                  + Convert.ToString(frames["seconds"], CultureInfo.InvariantCulture) + " s";

        static string Ms(object value) => Convert.ToString(value, CultureInfo.InvariantCulture) + " ms";

        /// <summary>leakCheck가 되풀이마다 잰 메모리를 CSV(바이트)로 남긴다. 통과했어도 남긴다: 빌드 사이의 추이를 볼 수 있다.</summary>
        public void SaveMemory(RuleResult result, string folder)
        {
            var csv = new StringBuilder("repeat,managed,native,process\n");
            for (int n = 0; n < result.memory.Count; n++)
            {
                csv.Append(n + 1);
                foreach (string layer in new[] { "managed", "native", "process" })
                    csv.Append(',').Append(Convert.ToString(result.memory[n][layer], CultureInfo.InvariantCulture));
                csv.Append('\n');
            }
            Directory.CreateDirectory(folder);
            memoryLog = Path.GetFullPath(Path.Combine(folder, Path.GetFileNameWithoutExtension(file) + ".memory.csv"));
            File.WriteAllText(memoryLog, csv.ToString());

            growth = null;
            if (result.growth == null) return;
            growth = new Dictionary<string, object>();
            foreach (KeyValuePair<string, double> pair in result.growth) growth[pair.Key] = Math.Round(pair.Value, 4);
        }

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
            if (memoryLog != null)
            {
                sb.Append("\n      memory:");
                if (growth != null)
                    foreach (KeyValuePair<string, object> pair in growth)
                        sb.Append(' ').Append(pair.Key).Append(' ').Append(((double)pair.Value).ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture))
                            .Append(" MB per repeat,");
                sb.Append(" history saved to ").Append(memoryLog);
            }
            if (frames != null) sb.Append("\n      frames: ").Append(FrameSummary(frames));
            if (device != null) sb.Append("\n      device: ").Append(DeviceMeter.Summary(device));
            if (ignored > 0) sb.Append("\n      layout: ").Append(ignored).Append(" issue(s) ignored by the exceptions");
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
