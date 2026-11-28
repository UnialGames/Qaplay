using System;
using System.Collections.Generic;
using System.Net.Http;

namespace Qaplay.Runner
{
    /// <summary>
    /// qaplay bugs jira ...: Jira를 잇는 준비. Unity의 설정 화면(Project Settings > qaplay)이 같은 명령을 부른다.
    ///   (없음)    지금 이어진 Jira와 이 PC의 로그인
    ///   login     사이트, 이메일, API 토큰(표준 입력이나 QAPLAY_JIRA_TOKEN)으로 로그인해 보고, 되면 이 PC에 넣어 둔다
    ///   projects  그 사이트에서 이 계정이 보는 프로젝트와 이슈 유형
    ///   connect   프로젝트와 이슈 유형을 확인하고 설정 파일의 issues에 적는다
    /// </summary>
    sealed class JiraCommand : IModuleCommand
    {
        readonly HttpMessageHandler m_Handler;

        public JiraCommand() : this(null)
        {
        }

        public JiraCommand(HttpMessageHandler handler)
        {
            m_Handler = handler;
        }

        public string Group => "bugs";
        public string Name => "jira";
        public string Usage =>
            "  qaplay bugs jira                     the Jira of the settings and who this PC logs in as\n" +
            "  qaplay bugs jira login --site SITE --email EMAIL   log in with an API token (asked, or from stdin or QAPLAY_JIRA_TOKEN)\n" +
            "                                       and keep it on this PC (" + JiraLogin.DefaultFile + ")\n" +
            "  qaplay bugs jira projects --site SITE   the projects this account can see, with their issue types\n" +
            "  qaplay bugs jira connect --site SITE --project KEY [--issue-type Bug]   send the bugs there (writes issues in the settings)";

        public int Run(Args args)
        {
            string action = args.Paths.Count > 1 ? args.Paths[1] : "";
            var tracker = new JiraTracker(m_Handler);
            switch (action)
            {
                case "": return Status(args);
                case "login": return Login(args, tracker);
                case "projects": return Projects(args, tracker);
                case "connect": return Connect(args, tracker);
                default: throw new UsageException("usage:\n" + Usage);
            }
        }

        static ProjectSettings Settings(Args args) => ProjectSettings.Load(args);

        static int Status(Args args)
        {
            Dictionary<string, object> issues = Settings(args).Issues;
            string site = issues != null && "jira".Equals(issues.GetValueOrDefault("type")) ? issues.GetValueOrDefault("site") as string : args.Get("site", null);
            if (site != null) site = JiraApi.SiteOf(site);
            JiraLogin login = site == null ? null : JiraLogin.For(site);
            bool connected = issues != null && "jira".Equals(issues.GetValueOrDefault("type"));
            Console.WriteLine(connected
                ? "bugs go to Jira: " + site + " project " + issues.GetValueOrDefault("project") + " (" + (issues.GetValueOrDefault("issueType") ?? "Bug") + ")"
                : "no Jira is connected in the settings");
            Console.WriteLine(site == null ? "" : login == null ? "this PC is not logged in to " + site
                : "this PC logs in to " + site + " as " + login.Email + (login.FromEnvironment ? " (QAPLAY_JIRA_EMAIL and QAPLAY_JIRA_TOKEN)" : ""));
            if (args.Json)
                Program.PrintJson(new Dictionary<string, object>
                {
                    ["connected"] = connected, ["site"] = site, ["project"] = connected ? issues.GetValueOrDefault("project") : null,
                    ["issueType"] = connected ? issues.GetValueOrDefault("issueType") ?? "Bug" : null,
                    ["email"] = login?.Email, ["fromEnvironment"] = login?.FromEnvironment ?? false, ["file"] = JiraLogin.DefaultFile,
                });
            return 0;
        }

        // 토큰은 명령줄에 받지 않는다(다른 프로그램이 볼 수 있다): 물어서 받거나, 표준 입력의 첫 줄, 또는 환경 변수
        static string Token()
        {
            string fromEnv = Environment.GetEnvironmentVariable("QAPLAY_JIRA_TOKEN");
            if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;
            if (Console.IsInputRedirected) return Console.In.ReadLine()?.Trim();
            Console.Error.Write("API token (it is not shown): ");
            var token = new System.Text.StringBuilder();
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace) { if (token.Length > 0) token.Length--; }
                else token.Append(key.KeyChar);
            }
            Console.Error.WriteLine();
            return token.ToString().Trim();
        }

        static int Failed(Args args, JiraException e)
        {
            Console.WriteLine(e.Message);
            if (args.Json) Program.PrintJson(new Dictionary<string, object> { ["ok"] = false, ["status"] = e.Status, ["message"] = e.Message, ["hint"] = e.Hint });
            return 1;
        }

        static int Login(Args args, JiraTracker tracker)
        {
            string site = JiraApi.SiteOf(args.Get("site", null));
            string email = args.Get("email", null)?.Trim() ?? throw new UsageException("give --email: the email of the Atlassian account");
            string token = Token();
            if (string.IsNullOrEmpty(token)) throw new UsageException("no API token was given: make one at " + JiraLogin.TokenPage);
            try
            {
                // 로그인이 되는지 먼저 본다. 안 되는 토큰은 넣어 두지 않는다
                var me = (Dictionary<string, object>)tracker.Api(site, new JiraLogin { Email = email, Token = token }).Get("/rest/api/3/myself");
                JiraLogin.Save(site, email, token);
                string name = me.GetValueOrDefault("displayName") as string ?? email;
                Console.WriteLine("logged in to " + site + " as " + name + " (" + email + "); kept on this PC in " + JiraLogin.DefaultFile);
                if (args.Json) Program.PrintJson(new Dictionary<string, object> { ["ok"] = true, ["site"] = site, ["name"] = name, ["email"] = email });
                return 0;
            }
            catch (JiraException e)
            {
                return Failed(args, e);
            }
        }

        static JiraApi Api(Args args, JiraTracker tracker, out string site)
        {
            site = JiraApi.SiteOf(args.Get("site", null));
            JiraLogin login = JiraLogin.For(site) ?? throw new UsageException("this PC is not logged in to " + site + ": run qaplay bugs jira login first");
            return tracker.Api(site, login);
        }

        static int Projects(Args args, JiraTracker tracker)
        {
            JiraApi api = Api(args, tracker, out string site);
            try
            {
                var projects = new List<object>();
                for (int start = 0; ; start += 50)
                {
                    var page = (Dictionary<string, object>)api.Get("/rest/api/3/project/search?expand=issueTypes&maxResults=50&startAt=" + start);
                    var values = (List<object>)page["values"];
                    foreach (object entry in values)
                    {
                        var project = (Dictionary<string, object>)entry;
                        var types = new List<object>();
                        if (project.GetValueOrDefault("issueTypes") is List<object> issueTypes)
                            foreach (object type in issueTypes)
                                if (!true.Equals(((Dictionary<string, object>)type).GetValueOrDefault("subtask"))) types.Add(((Dictionary<string, object>)type)["name"]);
                        projects.Add(new Dictionary<string, object> { ["key"] = project["key"], ["name"] = project["name"], ["issueTypes"] = types });
                        Console.WriteLine(project["key"] + "  " + project["name"] + "  (" + string.Join(", ", types) + ")");
                    }
                    if (true.Equals(page.GetValueOrDefault("isLast")) || values.Count == 0) break;
                }
                if (projects.Count == 0) Console.WriteLine("this account sees no project on " + site);
                if (args.Json) Program.PrintJson(new Dictionary<string, object> { ["ok"] = true, ["site"] = site, ["projects"] = projects });
                return 0;
            }
            catch (JiraException e)
            {
                return Failed(args, e);
            }
        }

        static int Connect(Args args, JiraTracker tracker)
        {
            JiraApi api = Api(args, tracker, out string site);
            string key = args.Get("project", null)?.Trim() ?? throw new UsageException("give --project: the key of the Jira project (QA)");
            string type = args.Get("issue-type", "Bug").Trim();
            try
            {
                var project = (Dictionary<string, object>)api.Get("/rest/api/3/project/" + Uri.EscapeDataString(key));
                var types = new List<string>();
                foreach (object entry in project.GetValueOrDefault("issueTypes") as List<object> ?? new List<object>())
                    types.Add((string)((Dictionary<string, object>)entry)["name"]);
                if (!types.Contains(type))
                    throw new UsageException("the project " + key + " has no issue type \"" + type + "\" (it has " + string.Join(", ", types) + "): give --issue-type");
                ProjectSettings settings = Settings(args);
                settings.Set("issues", new Dictionary<string, object> { ["type"] = "jira", ["site"] = site, ["project"] = (string)project["key"], ["issueType"] = type });
                settings.Save();
                Console.WriteLine("bugs go to " + site + " project " + project["key"] + " as " + type + " (written in " + settings.File + ")");
                if (args.Json) Program.PrintJson(new Dictionary<string, object> { ["ok"] = true, ["site"] = site, ["project"] = project["key"], ["issueType"] = type });
                return 0;
            }
            catch (JiraException e)
            {
                return Failed(args, e);
            }
        }
    }
}
