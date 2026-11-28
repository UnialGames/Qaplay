using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace Qaplay.Runner
{
    /// <summary>Jira가 거절했거나 닿지 않았다. 까닭과 사람이 할 일(Hint)을 함께 알린다.</summary>
    sealed class JiraException : IOException
    {
        public readonly int Status;
        public readonly string Hint;

        public JiraException(int status, string message, string hint) : base(message + (hint == null ? "" : " (" + hint + ")"))
        {
            Status = status;
            Hint = hint;
        }
    }

    /// <summary>
    /// 이 PC의 Jira 로그인 정보: 사이트마다 계정의 이메일과 API 토큰. 사용자 폴더의 .qaplay/jira.json에 둔다(저장소 밖, 그 PC의 그 사용자 것).
    /// CI에서는 환경 변수 QAPLAY_JIRA_EMAIL과 QAPLAY_JIRA_TOKEN이 먼저다(파일을 두지 않는다).
    /// </summary>
    sealed class JiraLogin
    {
        public const string TokenPage = "https://id.atlassian.com/manage-profile/security/api-tokens";

        public string Email, Token;
        public bool FromEnvironment;

        public static string DefaultFile
        {
            get
            {
                string home = Environment.GetEnvironmentVariable("QAPLAY_HOME");
                if (string.IsNullOrEmpty(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".qaplay");
                return Path.Combine(home, "jira.json");
            }
        }

        static Dictionary<string, object> ReadAll()
        {
            if (!File.Exists(DefaultFile)) return new Dictionary<string, object>();
            try
            {
                return Json.Parse(File.ReadAllText(DefaultFile)) as Dictionary<string, object> ?? new Dictionary<string, object>();
            }
            catch (FormatException)
            {
                // 내용은 보여 주지 않는다(토큰이 들어 있다)
                throw new UsageException(DefaultFile + " cannot be read: log in again (Unity: Project Settings > qaplay, or qaplay bugs jira login)");
            }
        }

        /// <summary>그 사이트의 로그인 정보. 없으면 null.</summary>
        public static JiraLogin For(string site)
        {
            string email = Environment.GetEnvironmentVariable("QAPLAY_JIRA_EMAIL"), token = Environment.GetEnvironmentVariable("QAPLAY_JIRA_TOKEN");
            if (!string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(token)) return new JiraLogin { Email = email, Token = token, FromEnvironment = true };
            return ReadAll().GetValueOrDefault(site) is Dictionary<string, object> saved && saved.GetValueOrDefault("email") is string e && saved.GetValueOrDefault("token") is string t
                ? new JiraLogin { Email = e, Token = t }
                : null;
        }

        /// <summary>그 사이트의 로그인 정보를 이 PC에 넣어 둔다. 다른 사이트의 것은 그대로다.</summary>
        public static void Save(string site, string email, string token)
        {
            Dictionary<string, object> all = ReadAll();
            all[site] = new Dictionary<string, object> { ["email"] = email, ["token"] = token };
            Directory.CreateDirectory(Path.GetDirectoryName(DefaultFile));
            File.WriteAllText(DefaultFile, Json.Write(all, true), new UTF8Encoding(false));
        }
    }

    /// <summary>Jira Cloud REST API(v3). 계정의 이메일과 API 토큰으로 로그인한다(Basic). 요청을 보내는 것(handler)은 넣어 줄 수 있다(검사는 가짜를 넣는다).</summary>
    sealed class JiraApi
    {
        readonly HttpClient m_Http;
        public readonly string Site;

        public JiraApi(string site, JiraLogin login, HttpMessageHandler handler)
        {
            Site = site;
            m_Http = handler == null ? new HttpClient() : new HttpClient(handler, false);
            m_Http.Timeout = TimeSpan.FromSeconds(30);
            m_Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(login.Email + ":" + login.Token)));
            m_Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        /// <summary>"team.atlassian.net", "https://team.atlassian.net/jira/..." 모두 "https://team.atlassian.net"이 된다. https만 받는다(토큰이 오간다).</summary>
        public static string SiteOf(string text)
        {
            string value = (text ?? "").Trim();
            if (value.Length == 0) throw new UsageException("give the Jira site, for example your-team.atlassian.net");
            if (!value.Contains("://")) value = "https://" + value;
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri) || uri.Scheme != "https" || uri.Host.Length == 0 || !uri.Host.Contains("."))
                throw new UsageException("not a Jira site address (it must be https): " + text);
            return "https://" + uri.Authority;
        }

        public object Get(string path) => Send(new HttpRequestMessage(HttpMethod.Get, Site + path), null);

        public object Post(string path, object body) =>
            Send(new HttpRequestMessage(HttpMethod.Post, Site + path) { Content = new StringContent(Json.Write(body), Encoding.UTF8, "application/json") }, null);

        public object Put(string path, object body) =>
            Send(new HttpRequestMessage(HttpMethod.Put, Site + path) { Content = new StringContent(Json.Write(body), Encoding.UTF8, "application/json") }, null);

        /// <summary>파일 하나를 이슈에 붙인다. Jira는 이 요청에 X-Atlassian-Token: no-check를 요구한다(없으면 거절한다).</summary>
        public void Attach(string key, string file)
        {
            var form = new MultipartFormDataContent();
            var content = new ByteArrayContent(File.ReadAllBytes(file));
            content.Headers.ContentType = new MediaTypeHeaderValue(file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "text/plain");
            form.Add(content, "file", Path.GetFileName(file));
            var request = new HttpRequestMessage(HttpMethod.Post, Site + "/rest/api/3/issue/" + Uri.EscapeDataString(key) + "/attachments") { Content = form };
            request.Headers.Add("X-Atlassian-Token", "no-check");
            Send(request, key);
        }

        object Send(HttpRequestMessage request, string issue)
        {
            HttpResponseMessage response;
            try
            {
                response = m_Http.SendAsync(request).GetAwaiter().GetResult();
            }
            catch (HttpRequestException e)
            {
                throw new JiraException(0, "cannot reach " + Site + ": " + e.Message, "check the site address, for example your-team.atlassian.net");
            }
            catch (TaskCanceledException)
            {
                throw new JiraException(0, Site + " did not answer in 30 seconds", "check the network");
            }
            string text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            int status = (int)response.StatusCode;
            if (status >= 200 && status < 300) return text.Trim().Length == 0 ? null : Json.Parse(text);
            throw new JiraException(status, "Jira answered " + status + ": " + Message(text), Hint(status, issue));
        }

        // Jira의 오류는 errorMessages(목록)와 errors(칸마다)로 온다
        static string Message(string text)
        {
            try
            {
                if (Json.Parse(text) is Dictionary<string, object> body)
                {
                    var parts = new List<string>();
                    if (body.GetValueOrDefault("errorMessages") is List<object> messages) foreach (object m in messages) parts.Add(m as string);
                    if (body.GetValueOrDefault("errors") is Dictionary<string, object> errors) foreach (KeyValuePair<string, object> e in errors) parts.Add(e.Key + ": " + e.Value);
                    if (parts.Count > 0) return string.Join("; ", parts);
                }
            }
            catch (FormatException)
            {
            }
            return text.Length > 200 ? text.Substring(0, 200) : text;
        }

        static string Hint(int status, string issue) => status switch
        {
            401 => "the email or the API token is wrong, or the token has expired: make a new one at " + JiraLogin.TokenPage
                   + " (\"Create API token\", the one without scopes) and log in again",
            403 => issue != null
                ? "this account may not add attachments to " + issue + ": ask a Jira admin for the Create attachments permission"
                : "this account may not do that in the project: ask a Jira admin for the Browse projects and Create issues permissions",
            404 => "it is not there, or this account cannot see it",
            _ => null,
        };
    }

    /// <summary>Jira(전략). Jira 모듈이 더하는 이슈 관리 도구다. 설정: issues = { type: jira, site, project, issueType }.</summary>
    sealed class JiraTracker : IIssueTracker
    {
        readonly HttpMessageHandler m_Handler;

        public JiraTracker() : this(null)
        {
        }

        /// <param name="handler">Jira에 요청을 보내는 것. null이면 진짜 Jira다.</param>
        public JiraTracker(HttpMessageHandler handler)
        {
            m_Handler = handler;
        }

        public string Type => "jira";
        public string Name => "Jira";

        public void Check(Dictionary<string, object> config)
        {
            foreach (string key in config.Keys)
                if (Array.IndexOf(new[] { "type", "site", "project", "issueType" }, key) < 0) throw new FormatException("unknown name \"" + key + "\" (use type, site, project, issueType)");
            if (!(config.GetValueOrDefault("site") is string site)) throw new FormatException("a jira tracker needs \"site\": your-team.atlassian.net");
            try
            {
                JiraApi.SiteOf(site);
            }
            catch (UsageException e)
            {
                throw new FormatException(e.Message);
            }
            if (!(config.GetValueOrDefault("project") is string project) || project.Trim().Length == 0) throw new FormatException("a jira tracker needs \"project\": the key of the project (QA)");
        }

        public IIssueConnection Open(Dictionary<string, object> config)
        {
            string site = JiraApi.SiteOf((string)config["site"]);
            JiraLogin login = JiraLogin.For(site) ?? throw new UsageException("this PC is not logged in to " + site
                                                                                   + ": log in in Unity (Project Settings > qaplay) or with qaplay bugs jira login");
            return new JiraConnection(new JiraApi(site, login, m_Handler), ((string)config["project"]).Trim(), config.GetValueOrDefault("issueType") as string ?? "Bug");
        }

        public JiraApi Api(string site, JiraLogin login) => new JiraApi(site, login, m_Handler);
    }

    sealed class JiraConnection : IIssueConnection
    {
        readonly JiraApi m_Api;
        readonly string m_Project, m_IssueType;

        public JiraConnection(JiraApi api, string project, string issueType)
        {
            m_Api = api;
            m_Project = project;
            m_IssueType = issueType;
        }

        static string Path(string key) => "/rest/api/3/issue/" + Uri.EscapeDataString(key);

        public IssueRef Create(BugDraft bug)
        {
            string summary = (bug.title ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (summary.Length == 0) summary = "qaplay bug " + bug.id;
            if (summary.Length > 250) summary = summary.Substring(0, 250); // Jira의 요약은 255자까지다
            var labels = new List<object> { "qaplay" };
            if (!string.IsNullOrEmpty(bug.kind)) labels.Add("qaplay-" + bug.kind);
            var fields = new Dictionary<string, object>
            {
                ["project"] = new Dictionary<string, object> { ["key"] = m_Project },
                ["issuetype"] = new Dictionary<string, object> { ["name"] = m_IssueType },
                ["summary"] = summary,
                ["description"] = Document(bug),
                ["labels"] = labels,
            };
            var made = (Dictionary<string, object>)m_Api.Post("/rest/api/3/issue", new Dictionary<string, object> { ["fields"] = fields });
            string key = (string)made["key"];
            return new IssueRef { key = key, url = m_Api.Site + "/browse/" + key };
        }

        public void Attach(string key, string file) => m_Api.Attach(key, file);

        public IssueState State(string key)
        {
            var issue = (Dictionary<string, object>)m_Api.Get(Path(key) + "?fields=status");
            var status = (Dictionary<string, object>)((Dictionary<string, object>)issue["fields"])["status"];
            string category = (status.GetValueOrDefault("statusCategory") as Dictionary<string, object>)?.GetValueOrDefault("key") as string;
            return new IssueState { name = (string)status["name"], done = category == "done" };
        }

        public bool Seen(string key, string note, bool reopen)
        {
            m_Api.Post(Path(key) + "/comment", new Dictionary<string, object> { ["body"] = Text(note) });
            if (!reopen) return true;
            // 다시 열기: 할 일(new) 쪽으로 가는 전환을 고른다. 워크플로마다 이름이 달라 범주로 찾는다
            var transitions = (List<object>)((Dictionary<string, object>)m_Api.Get(Path(key) + "/transitions"))["transitions"];
            Dictionary<string, object> back = null;
            foreach (string wanted in new[] { "new", "indeterminate" })
            {
                back = (Dictionary<string, object>)transitions.Find(t =>
                    ((((Dictionary<string, object>)t).GetValueOrDefault("to") as Dictionary<string, object>)?.GetValueOrDefault("statusCategory") as Dictionary<string, object>)
                    ?.GetValueOrDefault("key") as string == wanted);
                if (back != null) break;
            }
            if (back == null) return false; // 덧글은 남았다. 같은 덧글을 다시 달지 않게 실패로 멈추지 않는다
            m_Api.Post(Path(key) + "/transitions", new Dictionary<string, object> { ["transition"] = new Dictionary<string, object> { ["id"] = back["id"] } });
            m_Api.Put(Path(key), new Dictionary<string, object>
            {
                ["update"] = new Dictionary<string, object> { ["labels"] = new List<object> { new Dictionary<string, object> { ["add"] = "regression" } } },
            });
            return true;
        }

        // Jira의 글은 문서 형식(ADF)으로 보낸다: 줄마다 문단, 재현 스텝은 코드 블록
        static Dictionary<string, object> Document(BugDraft bug)
        {
            var content = new List<object>();
            foreach (string line in bug.lines) content.Add(Paragraph(line));
            if (bug.steps != null)
            {
                content.Add(Paragraph("Steps to reproduce (a qaplay rule file; replay it with qaplay run):"));
                content.Add(new Dictionary<string, object>
                {
                    ["type"] = "codeBlock", ["attrs"] = new Dictionary<string, object> { ["language"] = "yaml" },
                    ["content"] = new List<object> { new Dictionary<string, object> { ["type"] = "text", ["text"] = bug.steps } },
                });
            }
            return new Dictionary<string, object> { ["type"] = "doc", ["version"] = 1, ["content"] = content };
        }

        static Dictionary<string, object> Text(string text) =>
            new Dictionary<string, object> { ["type"] = "doc", ["version"] = 1, ["content"] = new List<object> { Paragraph(text) } };

        static Dictionary<string, object> Paragraph(string text) => new Dictionary<string, object>
        {
            ["type"] = "paragraph",
            ["content"] = text.Length == 0 ? new List<object>() : new List<object> { new Dictionary<string, object> { ["type"] = "text", ["text"] = text } },
        };
    }
}
