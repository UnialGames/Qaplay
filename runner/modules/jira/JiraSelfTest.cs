using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Qaplay.Runner
{
    /// <summary>
    /// Jira 모듈의 검사(qaplay selftest가 이 모듈이 설치되어 있으면 부른다). 진짜 Jira 대신 FakeJira가 답한다:
    /// 로그인 확인, 프로젝트와 이슈 유형, 이슈 만들기, 첨부, 상태, 덧글, 전환, 라벨. 진짜 Jira가 이 가짜와 같게 답하는지는 실제 사이트로 확인한다.
    /// </summary>
    sealed class JiraSelfTest : IModuleSelfTest
    {
        const string Site = "https://qa-team.atlassian.net", Email = "qa@example.com", Token = "good-token";

        static void Check(bool condition, string what)
        {
            if (!condition) throw new InvalidOperationException("selftest failed: " + what);
        }

        /// <summary>프로젝트 QA 하나를 가진 가짜 Jira.</summary>
        sealed class FakeJira : HttpMessageHandler
        {
            public sealed class Issue
            {
                public string key, summary, status = "To Do", category = "new";
                public Dictionary<string, object> fields;
                public readonly List<string> attachments = new List<string>(), comments = new List<string>(), labels = new List<string>();
            }

            public readonly Dictionary<string, Issue> issues = new Dictionary<string, Issue>();
            public int failAttachments;
            public bool refuseCreate, noWayBack;

            static HttpResponseMessage Answer(HttpStatusCode status, object body) =>
                new HttpResponseMessage(status) { Content = new StringContent(body == null ? "" : Json.Write(body), Encoding.UTF8, "application/json") };

            static HttpResponseMessage Refuse(int status, string message) =>
                Answer((HttpStatusCode)status, new Dictionary<string, object> { ["errorMessages"] = new List<object> { message }, ["errors"] = new Dictionary<string, object>() });

            static readonly List<object> Types = new List<object>
            {
                new Dictionary<string, object> { ["name"] = "Bug", ["subtask"] = false }, new Dictionary<string, object> { ["name"] = "Task", ["subtask"] = false },
                new Dictionary<string, object> { ["name"] = "Sub-task", ["subtask"] = true },
            };

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) => Task.FromResult(Handle(request));

            HttpResponseMessage Handle(HttpRequestMessage request)
            {
                if (!request.RequestUri.ToString().StartsWith(Site + "/rest/api/3/", StringComparison.Ordinal)) return Refuse(404, "no such site");
                string expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Email + ":" + Token));
                if (request.Headers.Authorization?.ToString() != expected) return Refuse(401, "Client must be authenticated to access this resource.");
                string path = request.RequestUri.AbsolutePath.Substring("/rest/api/3/".Length);
                string body = request.Content == null || request.Content is MultipartFormDataContent ? null : request.Content.ReadAsStringAsync().Result;
                var json = body == null || body.Length == 0 ? null : (Dictionary<string, object>)Json.Parse(body);
                string[] parts = path.Split('/');

                if (path == "myself") return Answer(HttpStatusCode.OK, new Dictionary<string, object> { ["displayName"] = "QA Runner", ["emailAddress"] = Email });
                if (path == "project/search")
                    return Answer(HttpStatusCode.OK, new Dictionary<string, object>
                    {
                        ["isLast"] = true, ["values"] = new List<object> { new Dictionary<string, object> { ["key"] = "QA", ["name"] = "QA 프로젝트", ["issueTypes"] = Types } },
                    });
                if (parts[0] == "project")
                    return parts[1] == "QA" ? Answer(HttpStatusCode.OK, new Dictionary<string, object> { ["key"] = "QA", ["issueTypes"] = Types }) : Refuse(404, "No project could be found with key '" + parts[1] + "'.");
                if (path == "issue" && request.Method == HttpMethod.Post)
                {
                    if (refuseCreate) return Refuse(403, "You do not have permission to create issues in this project.");
                    var fields = (Dictionary<string, object>)json["fields"];
                    if (!"QA".Equals(((Dictionary<string, object>)fields["project"])["key"]) || !"Bug".Equals(((Dictionary<string, object>)fields["issuetype"])["name"])
                        || !(fields["summary"] is string summary) || summary.Length == 0 || summary.Length > 255 || !"doc".Equals(((Dictionary<string, object>)fields["description"])["type"]))
                        return Answer(HttpStatusCode.BadRequest, new Dictionary<string, object> { ["errors"] = new Dictionary<string, object> { ["summary"] = "bad fields" } });
                    var issue = new Issue { key = "QA-" + (issues.Count + 1), summary = summary, fields = fields };
                    foreach (object label in (List<object>)fields["labels"]) issue.labels.Add((string)label);
                    issues[issue.key] = issue;
                    return Answer(HttpStatusCode.Created, new Dictionary<string, object> { ["id"] = "1000" + issues.Count, ["key"] = issue.key, ["self"] = Site + "/rest/api/3/issue/" + issue.key });
                }
                if (parts[0] == "issue" && parts.Length >= 2)
                {
                    if (!issues.TryGetValue(Uri.UnescapeDataString(parts[1]), out Issue issue)) return Refuse(404, "Issue does not exist or you do not have permission to see it.");
                    string what = parts.Length > 2 ? parts[2] : "";
                    if (what == "attachments")
                    {
                        // 진짜 Jira처럼: 이 머리말이 없으면 거절한다
                        if (!request.Headers.TryGetValues("X-Atlassian-Token", out IEnumerable<string> values) || !new List<string>(values).Contains("no-check"))
                            return Refuse(403, "XSRF check failed");
                        if (failAttachments > 0)
                        {
                            failAttachments--;
                            return Refuse(500, "attachment storage is unavailable");
                        }
                        foreach (HttpContent part in (MultipartFormDataContent)request.Content)
                            if (part.Headers.ContentDisposition?.Name?.Trim('"') == "file") issue.attachments.Add(part.Headers.ContentDisposition.FileName.Trim('"'));
                        return Answer(HttpStatusCode.OK, new List<object>());
                    }
                    if (what == "comment")
                    {
                        var doc = (Dictionary<string, object>)json["body"];
                        var paragraph = (Dictionary<string, object>)((List<object>)doc["content"])[0];
                        issue.comments.Add((string)((Dictionary<string, object>)((List<object>)paragraph["content"])[0])["text"]);
                        return Answer(HttpStatusCode.Created, new Dictionary<string, object> { ["id"] = "1" });
                    }
                    if (what == "transitions" && request.Method == HttpMethod.Get)
                        return Answer(HttpStatusCode.OK, new Dictionary<string, object>
                        {
                            // noWayBack: 끝난 뒤로는 갈 곳이 없는 워크플로
                            ["transitions"] = noWayBack ? new List<object>() : new List<object>
                            {
                                Transition("31", "Done", "done"), Transition("21", "In Progress", "indeterminate"), Transition("11", "To Do", "new"),
                            },
                        });
                    if (what == "transitions")
                    {
                        string id = (string)((Dictionary<string, object>)json["transition"])["id"];
                        (issue.status, issue.category) = id == "11" ? ("To Do", "new") : id == "21" ? ("In Progress", "indeterminate") : ("Done", "done");
                        return Answer(HttpStatusCode.NoContent, null);
                    }
                    if (request.Method == HttpMethod.Put)
                    {
                        foreach (object change in (List<object>)((Dictionary<string, object>)json["update"])["labels"])
                            issue.labels.Add((string)((Dictionary<string, object>)change)["add"]);
                        return Answer(HttpStatusCode.NoContent, null);
                    }
                    return Answer(HttpStatusCode.OK, new Dictionary<string, object>
                    {
                        ["key"] = issue.key,
                        ["fields"] = new Dictionary<string, object>
                        {
                            ["status"] = new Dictionary<string, object> { ["name"] = issue.status, ["statusCategory"] = new Dictionary<string, object> { ["key"] = issue.category } },
                        },
                    });
                }
                return Refuse(404, "no such resource: " + path);
            }

            static object Transition(string id, string name, string category) => new Dictionary<string, object>
            {
                ["id"] = id, ["name"] = name, ["to"] = new Dictionary<string, object> { ["name"] = name, ["statusCategory"] = new Dictionary<string, object> { ["key"] = category } },
            };
        }

        static int Quiet(Func<int> command)
        {
            TextWriter was = Console.Out;
            Console.SetOut(TextWriter.Null);
            try
            {
                return command();
            }
            finally
            {
                Console.SetOut(was);
            }
        }

        public void Run()
        {
            Check(JiraApi.SiteOf("qa-team.atlassian.net") == Site && JiraApi.SiteOf(" https://qa-team.atlassian.net/jira/software/projects/QA ") == Site, "the site is taken from what people paste");
            foreach (string bad in new[] { "http://qa-team.atlassian.net", "", "not a site" })
            {
                try { JiraApi.SiteOf(bad); Check(false, "not a site: " + bad); }
                catch (UsageException) { }
            }

            string folder = Path.Combine(Path.GetTempPath(), "qaplay-selftest-" + Guid.NewGuid().ToString("N"));
            string home = Environment.GetEnvironmentVariable("QAPLAY_HOME"), envEmail = Environment.GetEnvironmentVariable("QAPLAY_JIRA_EMAIL"), envToken = Environment.GetEnvironmentVariable("QAPLAY_JIRA_TOKEN");
            try
            {
                Directory.CreateDirectory(folder);
                Environment.SetEnvironmentVariable("QAPLAY_HOME", Path.Combine(folder, "home"));
                Environment.SetEnvironmentVariable("QAPLAY_JIRA_EMAIL", null);
                var jira = new FakeJira();
                var command = new JiraCommand(jira);
                string settingsFile = Path.Combine(folder, "qaplay.json");
                string[] With(params string[] words)
                {
                    var all = new List<string>(words) { "--settings", settingsFile };
                    return all.ToArray();
                }

                // ---- 로그인: 안 되는 토큰은 넣어 두지 않는다 ----
                Environment.SetEnvironmentVariable("QAPLAY_JIRA_TOKEN", "wrong-token");
                Check(Quiet(() => command.Run(new Args(With("bugs", "jira", "login", "--site", "qa-team.atlassian.net", "--email", Email)))) == 1 && !File.Exists(JiraLogin.DefaultFile),
                    "a wrong token is refused and not kept");
                Environment.SetEnvironmentVariable("QAPLAY_JIRA_TOKEN", Token);
                Check(Quiet(() => command.Run(new Args(With("bugs", "jira", "login", "--site", "qa-team.atlassian.net", "--email", Email)))) == 0, "a good token logs in");
                Environment.SetEnvironmentVariable("QAPLAY_JIRA_TOKEN", null);
                Check(JiraLogin.DefaultFile.StartsWith(Path.Combine(folder, "home")) && JiraLogin.For(Site)?.Email == Email && !JiraLogin.For(Site).FromEnvironment,
                    "the login is kept in the user folder, outside the project");

                // ---- 프로젝트 고르기와 잇기 ----
                Check(Quiet(() => command.Run(new Args(With("bugs", "jira", "projects", "--site", Site)))) == 0, "the projects can be listed");
                try { Quiet(() => command.Run(new Args(With("bugs", "jira", "connect", "--site", Site, "--project", "QA", "--issue-type", "Story")))); Check(false, "an issue type the project lacks is refused"); }
                catch (UsageException e) { Check(e.Message.Contains("Bug, Task, Sub-task"), "and the message lists the ones it has"); }
                Check(Quiet(() => command.Run(new Args(With("bugs", "jira", "connect", "--site", Site, "--project", "NOPE")))) == 1, "a project that is not there is reported");
                Check(Quiet(() => command.Run(new Args(With("bugs", "jira", "connect", "--site", Site, "--project", "QA")))) == 0, "a project is connected");
                ProjectSettings settings = ProjectSettings.Load(settingsFile);
                string written = File.ReadAllText(settingsFile);
                Check("jira".Equals(settings.Issues["type"]) && "QA".Equals(settings.Issues["project"]) && "Bug".Equals(settings.Issues["issueType"]) && !written.Contains(Token),
                    "the settings get the site and project, never the token");

                // ---- 맞추기: 등록과 첨부 ----
                var store = new BugStore(Path.Combine(folder, "bugs"));
                Dictionary<string, object> crash = store.Create("crash", "Crash while running shop");
                crash["platform"] = "Android";
                store.Save(crash);
                File.WriteAllText(Path.Combine(store.FolderOf("0001"), "steps.yaml"), "name: shop\n---\n- tapOn: 구매\n");
                File.WriteAllText(Path.Combine(store.FolderOf("0001"), "log.txt"), "the game log\n");
                File.WriteAllBytes(Path.Combine(store.FolderOf("0001"), "screenshot.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
                store.Create("manual", new string('긴', 300)); // Jira의 요약 한도(255자)보다 긴 제목

                var tracker = new JiraTracker(jira);
                var trackers = new IssueTrackers(tracker);
                var said = new List<string>();
                IIssueConnection connection = IssueSync.Open(settings, trackers, out IIssueTracker used);
                Check(used == tracker && IssueSync.Run(store, tracker, connection, said.Add), "the bugs are sent");
                FakeJira.Issue first = jira.issues["QA-1"];
                Check(jira.issues.Count == 2 && first.summary == "Crash while running shop" && jira.issues["QA-2"].summary.Length == 250, "an issue per bug, titles cut to what Jira takes");
                Check(first.attachments.Count == 3 && first.attachments.Contains("screenshot.png") && first.labels.Contains("qaplay") && first.labels.Contains("qaplay-crash"),
                    "the screen, log and steps are attached, with labels");
                Check(Json.Write(first.fields["description"]).Contains("\"codeBlock\"") && Json.Write(first.fields["description"]).Contains("- tapOn: 구매"), "the steps go in as a code block");
                var linked = (Dictionary<string, object>)store.Load("0001")["issue"];
                Check("QA-1".Equals(linked["key"]) && (Site + "/browse/QA-1").Equals(linked["url"]) && "To Do".Equals(linked["status"]), "the bug knows its issue");
                Check(said.Exists(line => line.StartsWith("bug 0001 -> QA-1")), "and says where it went");

                Check(IssueSync.Run(store, tracker, connection, said.Add) && jira.issues.Count == 2 && first.attachments.Count == 3, "syncing again sends nothing twice");

                // ---- Jira에서 고쳤다고 하면 이 PC에서도 resolved ----
                (first.status, first.category) = ("Done", "done");
                IssueSync.Run(store, tracker, connection, said.Add);
                Check("resolved".Equals(store.Load("0001")["status"]), "done in Jira is resolved here");

                // ---- 다시 났다: 덧글, 다시 열기, 회귀 라벨. 새 로그도 올린다 ----
                Dictionary<string, object> again = store.Load("0001");
                again["count"] = 2;
                again["lastSeen"] = BugStore.Now();
                BugStore.Reopen(again);
                store.Save(again);
                File.WriteAllText(Path.Combine(store.FolderOf("0001"), "log.txt"), "the log of the second crash\n");
                File.SetLastWriteTimeUtc(Path.Combine(store.FolderOf("0001"), "log.txt"), DateTime.UtcNow.AddMinutes(1));
                Check(IssueSync.Run(store, tracker, connection, said.Add), "a bug seen again is synced");
                Check(first.category == "new" && first.labels.Contains("regression") && first.comments.Count == 1 && first.comments[0].Contains("2 time(s)"),
                    "it is reopened in Jira as a regression, with a comment");
                Check(first.attachments.Count == 4 && "open".Equals(store.Load("0001")["status"]) && !store.Load("0001").ContainsKey("reopenPending"), "the new log is attached; open here");

                // ---- 끝난 뒤로 갈 곳이 없는 워크플로: 덧글만 남기고, 같은 덧글을 되풀이하지 않는다 ----
                (first.status, first.category) = ("Closed", "done");
                jira.noWayBack = true;
                again = store.Load("0001");
                again["count"] = 3;
                store.Save(again);
                Check(IssueSync.Run(store, tracker, connection, said.Add) && first.comments.Count == 2 && first.category == "done"
                      && said[said.Count - 1].Contains("reopen it in Jira by hand"), "it says to reopen by hand");
                Check(IssueSync.Run(store, tracker, connection, said.Add) && first.comments.Count == 2, "and the comment is not added again");
                jira.noWayBack = false;

                // ---- 보내지 못하면 이 PC의 것은 그대로, 다음에 남은 것만 ----
                store.Create("exception", "NullReferenceException in Shop");
                File.WriteAllText(Path.Combine(store.FolderOf("0003"), "log.txt"), "stack\n");
                jira.failAttachments = 1;
                Check(!IssueSync.Run(store, tracker, connection, said.Add) && said[said.Count - 1].Contains("kept here"), "a failure stops the sync and says the bug is kept");
                Check(jira.issues.Count == 3 && store.Load("0003").ContainsKey("issue"), "the issue was made before the attachment failed, and it is remembered");
                Check(IssueSync.Run(store, tracker, connection, said.Add) && jira.issues.Count == 3 && jira.issues["QA-3"].attachments.Count == 1, "the next sync sends only what is missing");

                jira.refuseCreate = true;
                store.Create("freeze", "Freeze while running gift");
                Check(!IssueSync.Run(store, tracker, connection, said.Add) && !store.Load("0004").ContainsKey("issue")
                      && said[said.Count - 1].Contains("Create issues"), "a refused issue is not linked, and the hint says which permission");

                // ---- 로그인하지 않은 PC ----
                Environment.SetEnvironmentVariable("QAPLAY_HOME", Path.Combine(folder, "other-pc"));
                try { IssueSync.Open(settings, trackers, out _); Check(false, "a PC that is not logged in cannot open Jira"); }
                catch (UsageException e) { Check(e.Message.Contains("not logged in") && e.Message.Contains("Project Settings > qaplay"), "and is told where to log in"); }
                // CI: 환경 변수로
                Environment.SetEnvironmentVariable("QAPLAY_JIRA_EMAIL", Email);
                Environment.SetEnvironmentVariable("QAPLAY_JIRA_TOKEN", Token);
                Check(IssueSync.Open(settings, trackers, out _) != null && JiraLogin.For(Site).FromEnvironment, "CI logs in with QAPLAY_JIRA_EMAIL and QAPLAY_JIRA_TOKEN");
            }
            finally
            {
                Environment.SetEnvironmentVariable("QAPLAY_HOME", home);
                Environment.SetEnvironmentVariable("QAPLAY_JIRA_EMAIL", envEmail);
                Environment.SetEnvironmentVariable("QAPLAY_JIRA_TOKEN", envToken);
                Directory.Delete(folder, true);
            }
        }
    }
}
