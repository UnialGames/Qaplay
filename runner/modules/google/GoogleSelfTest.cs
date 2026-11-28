using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Qaplay.Runner
{
    /// <summary>
    /// Google 모듈의 검사(qaplay selftest가 이 모듈이 설치되어 있으면 부른다). 진짜 Google 대신 FakeGoogle이 답한다: 로그인(서명 확인), 탭 목록, 값 읽기, 값 쓰기, 탭 만들기, 크기 늘리기.
    /// 진짜 Google이 이 가짜와 같게 답하는지는 여기서 알 수 없다. 그것은 실제 시트로 확인한다.
    /// </summary>
    sealed class GoogleSelfTest : IModuleSelfTest
    {
        const string Id = "1AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_test";
        const string Email = "qa-runner@sample-project.iam.gserviceaccount.com";

        static void Check(bool condition, string what)
        {
            if (!condition) throw new InvalidOperationException("selftest failed: " + what);
        }

        /// <summary>시트 하나를 가진 가짜 Google.</summary>
        sealed class FakeGoogle : HttpMessageHandler
        {
            public sealed class Tab
            {
                public string title;
                public int sheetId, rowCount = 1000, columnCount = 26;
                public readonly List<List<object>> rows = new List<List<object>>();
            }

            public RSA publicKey;
            /// <summary>none: 공유하지 않음, reader: 보기만, writer: 편집.</summary>
            public string shared = "writer";
            /// <summary>키의 프로젝트에서 Sheets API가 꺼져 있다(진짜 Google처럼 까닭과 켜는 페이지를 붙여 거절한다).</summary>
            public bool apiOff;
            public readonly List<Tab> tabs = new List<Tab>();
            public int logins, cellsWritten, grows;

            public Tab Add(string title, params string[][] rows)
            {
                var tab = new Tab { title = title, sheetId = tabs.Count * 7 };
                foreach (string[] row in rows) tab.rows.Add(new List<object>(row));
                tabs.Add(tab);
                return tab;
            }

            public string Cell(string title, int row, int column)
            {
                Tab tab = tabs.Find(t => t.title == title);
                return tab == null || row >= tab.rows.Count || column >= tab.rows[row].Count ? "" : Convert.ToString(tab.rows[row][column], CultureInfo.InvariantCulture);
            }

            static HttpResponseMessage Answer(HttpStatusCode status, object body) =>
                new HttpResponseMessage(status) { Content = new StringContent(Json.Write(body), Encoding.UTF8, "application/json") };

            static HttpResponseMessage Refuse(int status, string message) => Answer((HttpStatusCode)status, new Dictionary<string, object>
            {
                ["error"] = new Dictionary<string, object> { ["code"] = status, ["message"] = message },
            });

            static string Decode(string base64Url) => Encoding.UTF8.GetString(Bytes(base64Url));

            static byte[] Bytes(string base64Url)
            {
                string text = base64Url.Replace('-', '+').Replace('_', '/');
                return Convert.FromBase64String(text + new string('=', (4 - text.Length % 4) % 4));
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) => Task.FromResult(Handle(request));

            HttpResponseMessage Handle(HttpRequestMessage request)
            {
                string url = request.RequestUri.AbsoluteUri, body = request.Content == null ? "" : request.Content.ReadAsStringAsync().Result;
                if (url == "https://oauth2.googleapis.com/token")
                {
                    // 서명이 그 키의 것이고, 누구로 무엇을 하려는지가 맞아야 토큰을 준다
                    string assertion = null;
                    foreach (string pair in body.Split('&'))
                        if (pair.StartsWith("assertion=")) assertion = Uri.UnescapeDataString(pair.Substring(10));
                    string[] parts = (assertion ?? "").Split('.');
                    if (!body.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer") || parts.Length != 3
                        || !publicKey.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Bytes(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                        return Answer(HttpStatusCode.BadRequest, new Dictionary<string, object> { ["error"] = "invalid_grant", ["error_description"] = "Invalid JWT Signature." });
                    var claims = (Dictionary<string, object>)Json.Parse(Decode(parts[1]));
                    if (!Email.Equals(claims["iss"]) || !"https://www.googleapis.com/auth/spreadsheets".Equals(claims["scope"]) || !url.Equals(claims["aud"])
                        || Convert.ToDouble(claims["exp"]) - Convert.ToDouble(claims["iat"]) != 3600 || !Decode(parts[0]).Contains("RS256"))
                        return Answer(HttpStatusCode.BadRequest, new Dictionary<string, object> { ["error"] = "invalid_grant" });
                    logins++;
                    return Answer(HttpStatusCode.OK, new Dictionary<string, object> { ["access_token"] = "token-1", ["expires_in"] = 3600, ["token_type"] = "Bearer" });
                }

                const string root = "https://sheets.googleapis.com/v4/spreadsheets/";
                if (!url.StartsWith(root)) return Refuse(404, "no such API");
                if (!request.Headers.TryGetValues("Authorization", out IEnumerable<string> auth) || string.Join("", auth) != "Bearer token-1") return Refuse(401, "Request had invalid authentication credentials.");
                string rest = url.Substring(root.Length);
                if (!rest.StartsWith(Id)) return Refuse(404, "Requested entity was not found.");
                if (apiOff)
                    return Answer(HttpStatusCode.Forbidden, new Dictionary<string, object>
                    {
                        ["error"] = new Dictionary<string, object>
                        {
                            ["code"] = 403, ["status"] = "PERMISSION_DENIED",
                            ["message"] = "Google Sheets API has not been used in project 516115 before or it is disabled. Enable it by visiting https://console.developers.google.com/apis/api/sheets.googleapis.com/overview?project=516115 then retry.",
                            ["details"] = new List<object>
                            {
                                new Dictionary<string, object>
                                {
                                    ["@type"] = "type.googleapis.com/google.rpc.ErrorInfo", ["reason"] = "SERVICE_DISABLED", ["domain"] = "googleapis.com",
                                    ["metadata"] = new Dictionary<string, object> { ["activationUrl"] = "https://console.developers.google.com/apis/api/sheets.googleapis.com/overview?project=516115" },
                                },
                            },
                        },
                    });
                if (shared == "none") return Refuse(403, "The caller does not have permission");
                rest = rest.Substring(Id.Length);

                if (request.Method == HttpMethod.Get && rest.StartsWith("?fields="))
                    return Answer(HttpStatusCode.OK, new Dictionary<string, object>
                    {
                        ["sheets"] = tabs.ConvertAll(tab => (object)new Dictionary<string, object>
                        {
                            ["properties"] = new Dictionary<string, object>
                            {
                                ["sheetId"] = tab.sheetId, ["title"] = tab.title,
                                ["gridProperties"] = new Dictionary<string, object> { ["rowCount"] = tab.rowCount, ["columnCount"] = tab.columnCount },
                            },
                        }),
                    });

                if (request.Method == HttpMethod.Get && rest.StartsWith("/values/"))
                {
                    string range = Uri.UnescapeDataString(rest.Substring(8, rest.IndexOf('?') - 8));
                    // 탭 전체('TC')나 첫 줄('TC'!1:1)
                    bool firstRow = range.EndsWith("!1:1");
                    string name = firstRow ? range.Substring(0, range.Length - 4) : range;
                    Tab tab = tabs.Find(t => "'" + t.title.Replace("'", "''") + "'" == name);
                    if (tab == null) return Refuse(400, "Unable to parse range: " + range);
                    // 진짜처럼: 모두 글자로, 뒤쪽의 빈 칸과 빈 줄은 뺀다
                    var values = new List<object>();
                    foreach (List<object> row in firstRow ? tab.rows.GetRange(0, Math.Min(1, tab.rows.Count)) : tab.rows)
                    {
                        var cells = row.ConvertAll(cell => (object)Convert.ToString(cell, CultureInfo.InvariantCulture));
                        while (cells.Count > 0 && (string)cells[cells.Count - 1] == "") cells.RemoveAt(cells.Count - 1);
                        values.Add(cells);
                    }
                    while (values.Count > 0 && ((List<object>)values[values.Count - 1]).Count == 0) values.RemoveAt(values.Count - 1);
                    var answer = new Dictionary<string, object> { ["range"] = range, ["majorDimension"] = "ROWS" };
                    if (values.Count > 0) answer["values"] = values;
                    return Answer(HttpStatusCode.OK, answer);
                }

                if (request.Method != HttpMethod.Post) return Refuse(404, "no such method");
                if (shared != "writer") return Refuse(403, "The caller does not have permission");
                var json = (Dictionary<string, object>)Json.Parse(body);

                if (rest == "/values:batchUpdate")
                {
                    if (!"RAW".Equals(json["valueInputOption"])) return Refuse(400, "expected RAW");
                    foreach (object entry in (List<object>)json["data"])
                    {
                        var data = (Dictionary<string, object>)entry;
                        string range = (string)data["range"];
                        int bang = range.LastIndexOf('!');
                        Tab tab = tabs.Find(t => "'" + t.title.Replace("'", "''") + "'" == range.Substring(0, bang));
                        if (tab == null) return Refuse(400, "Unable to parse range: " + range);
                        string a1 = range.Substring(bang + 1);
                        int letters = 0, column = 0;
                        while (char.IsLetter(a1[letters])) column = column * 26 + (a1[letters++] - 'A' + 1);
                        int row = int.Parse(a1.Substring(letters), CultureInfo.InvariantCulture) - 1;
                        column--;
                        if (row >= tab.rowCount || column >= tab.columnCount) return Refuse(400, "Range (" + range + ") exceeds grid limits.");
                        while (tab.rows.Count <= row) tab.rows.Add(new List<object>());
                        while (tab.rows[row].Count <= column) tab.rows[row].Add("");
                        tab.rows[row][column] = ((List<object>)((List<object>)data["values"])[0])[0];
                        cellsWritten++;
                    }
                    return Answer(HttpStatusCode.OK, new Dictionary<string, object> { ["spreadsheetId"] = Id });
                }

                if (rest == ":batchUpdate")
                {
                    foreach (object entry in (List<object>)json["requests"])
                    {
                        var one = (Dictionary<string, object>)entry;
                        if (one.TryGetValue("addSheet", out object add))
                            Add((string)((Dictionary<string, object>)((Dictionary<string, object>)add)["properties"])["title"]);
                        else if (one.TryGetValue("appendDimension", out object append))
                        {
                            var grow = (Dictionary<string, object>)append;
                            Tab tab = tabs.Find(t => t.sheetId == Convert.ToInt32(grow["sheetId"]));
                            if ("ROWS".Equals(grow["dimension"])) tab.rowCount += Convert.ToInt32(grow["length"]);
                            else tab.columnCount += Convert.ToInt32(grow["length"]);
                            grows++;
                        }
                        else return Refuse(400, "unknown request");
                    }
                    return Answer(HttpStatusCode.OK, new Dictionary<string, object> { ["spreadsheetId"] = Id });
                }
                return Refuse(404, "no such method");
            }
        }

        public void Run()
        {
            Check(GoogleSheet.IdOf("https://docs.google.com/spreadsheets/d/" + Id + "/edit?gid=0#gid=0") == Id && GoogleSheet.IdOf(" " + Id + " ") == Id, "the id is taken from the address");
            try { GoogleSheet.IdOf("https://example.com/sheet"); Check(false, "an address that is not a sheet is refused"); }
            catch (UsageException) { }
            Check(GoogleApi.A1(0, 0) == "A1" && GoogleApi.A1(0, 25) == "Z1" && GoogleApi.A1(0, 26) == "AA1" && GoogleApi.A1(9, 701) == "ZZ10" && GoogleApi.A1(0, 702) == "AAA1", "cells are named like A1");

            string folder = Path.Combine(Path.GetTempPath(), "qaplay-selftest-" + Guid.NewGuid().ToString("N"));
            string oldHome = Environment.GetEnvironmentVariable("QAPLAY_HOME"), oldKey = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
            using RSA rsa = RSA.Create(2048);
            var google = new FakeGoogle { publicKey = rsa };
            try
            {
                Directory.CreateDirectory(Path.Combine(folder, "rules"));
                Environment.SetEnvironmentVariable("QAPLAY_HOME", Path.Combine(folder, "home"));
                Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", null);
                // 가짜 Google을 Google 종류에 넣어 준다. 나머지는 러너가 실제로 쓰는 것과 같다
                var providers = new SheetProviders(new CsvProvider(), new GoogleProvider(google));
                File.WriteAllText(Path.Combine(folder, "rules", "buy.yaml"), "tc: TC-1\n---\n- tapOn: 구매\n");
                string downloaded = Path.Combine(folder, "downloaded-key.json");
                File.WriteAllText(downloaded, Json.Write(new Dictionary<string, object>
                {
                    ["type"] = "service_account", ["client_email"] = Email, ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(), ["token_uri"] = "https://oauth2.googleapis.com/token",
                }));

                // 키가 아닌 파일은 내용을 드러내지 않고 거절한다
                string notKey = Path.Combine(folder, "other.json");
                File.WriteAllText(notKey, "{\"type\": \"authorized_user\", \"refresh_token\": \"SECRET-VALUE\"}");
                try { GoogleKey.Read(notKey); Check(false, "a file that is not a service account key is refused"); }
                catch (UsageException e) { Check(!e.Message.Contains("SECRET-VALUE"), "the message does not show what is in the key file"); }

                // ---- 팀이 쓰던 시트: 한국어 칸 이름, 칸이 넷뿐인 탭. 옵션만으로(묻지 않고) 준비한다 ----
                FakeGoogle.Tab tc = google.Add("TC", new[] { "TC 번호", "화면", "내용", "기대 결과" }, new[] { "TC-1", "상점", "구매를 누른다", "골드가 100 준다" },
                    new[] { "TC-2", "상점", "판매를 누른다", "골드가 50 는다" });
                tc.columnCount = 4;
                string settings = Path.Combine(folder, "qaplay-sheet.json");
                var said = new List<string>();
                var init = new Args(new[]
                {
                    "tc", "init", "--settings", settings, "--credentials", downloaded, "--spreadsheet", "https://docs.google.com/spreadsheets/d/" + Id + "/edit",
                    "--rules", Path.Combine(folder, "rules"),
                });
                Check(SheetSetup.Run(init, providers, question => null, said.Add) == 0, "init with every answer given as an option needs nobody");
                Check(File.Exists(GoogleKey.DefaultFile) && GoogleKey.Read(GoogleKey.DefaultFile).Email == Email, "the key is kept outside the project");
                string written = File.ReadAllText(settings);
                Check(!written.Contains("private_key") && !written.Contains("downloaded-key") && !written.Contains("google-key"), "the settings file holds no secret and no path to one");
                SheetConfig config = SheetConfig.Load(settings, providers);
                Check(config.Type == "google" && GoogleSheet.IdOf(config.Spreadsheet) == Id && config.Tc == "TC" && config.Summary == "요약" && config.Column("tc") == "TC 번호"
                      && config.Column("expected") == "기대 결과" && config.Column("rule") == "규칙 상태" && config.Label("passed") == "통과",
                    "a sheet with Korean columns gets Korean settings, and its own column names are found");
                Check(google.Cell("TC", 0, 4) == "규칙 상태" && google.Cell("TC", 1, 4) == "있음" && google.Cell("TC", 2, 4) == "없음" && google.grows == 1 && tc.columnCount == 10,
                    "init writes the tool's columns, growing the tab when it is too narrow");
                Check(said.Exists(line => line.Contains(Email)) && said.Exists(line => line.StartsWith("Done.")), "init says which address to share the sheet with");
                try { SheetSetup.Run(init, providers, question => null, said.Add); Check(false, "init does not overwrite settings"); }
                catch (UsageException) { }

                // ---- 실행 결과를 적는다 ----
                var sheet = providers.Sheet(config);
                Check(sheet.List().Count == 2 && sheet.List()[0].cells["내용"].Equals("구매를 누른다"), "test cases are read from the sheet");
                var ran = new Outcome { file = "buy.yaml", rule = RuleReader.Read("tc: TC-1\n---\n- tapOn: a\n"), passed = true };
                int before = google.cellsWritten;
                sheet.Record(new List<Outcome> { ran }, "com.game 1.2", "Android", null, "20261128-170758");
                Check(google.Cell("TC", 1, 5) == "통과" && google.Cell("TC", 1, 6).StartsWith("com.game 1.2 / Android / ") && google.Cell("TC", 1, 8) == "20261128-170758"
                      && google.Cell("TC", 1, 1) == "상점" && google.Cell("TC", 2, 5) == "", "results go to the row of the TC, human cells stay");
                Check(google.Cell("요약", 0, 0) == "빌드" && google.Cell("요약", 1, 0) == "com.game 1.2" && google.Cell("요약", 1, 7) == "100%"
                      && google.tabs.Find(t => t.title == "요약").rows[1][3] is double, "the summary tab is created; counts are written as numbers");
                // 같은 결과를 다시 적으면 TC 탭에는 바뀐 칸이 없다: 요약 줄 여덟 칸만 더 쓴다
                int afterFirst = google.cellsWritten;
                sheet.Record(new List<Outcome> { ran }, "com.game 1.2", "Android", null, "20261128-170758");
                Check(afterFirst - before == 3 + 8 + 8 && google.cellsWritten - afterFirst == 8 && google.Cell("요약", 2, 0) == "com.game 1.2", "only the cells that changed are written");
                // 여기까지 로그인은 세 번이다: init, init이 끝에 하는 쓰기 확인, 그리고 위의 sheet(목록 두 번과 기록 두 번이 한 번으로)
                Check(google.logins == 3, "one sign-in per command, not per request");

                // 0으로 시작하는 번호와, 수식처럼 보이는 글자는 글자 그대로 들어간다
                var store = providers.Get(config.Type).Open(config);
                List<List<string>> table = store.Read("tc");
                table[2][1] = "=1+1";
                table[2][2] = "007";
                store.Write("tc", table);
                FakeGoogle.Tab stored = google.tabs.Find(t => t.title == "TC");
                Check("=1+1".Equals(stored.rows[2][1]) && "007".Equals(stored.rows[2][2]), "text is written as it is (RAW), never as a formula");

                // ---- 보기 권한만 있으면: 읽기는 되고, 쓰기는 무엇을 하면 되는지와 함께 거절된다 ----
                google.shared = "reader";
                Check(providers.Sheet(config).List().Count == 2, "a sheet shared for viewing can be read");
                int untouched = google.cellsWritten;
                providers.Sheet(config).Record(null, null, null);
                Check(google.cellsWritten == untouched, "with nothing to change, nothing is sent (so nothing is refused)");
                table[1][1] = "다른 화면";
                try { providers.Get(config.Type).Open(config).Write("tc", table); Check(false, "writing without edit access is refused"); }
                catch (GoogleException e) { Check(e.Status == 403 && e.Message.Contains("does not have permission") && e.Message.Contains("as an Editor"), "the refusal says what to do"); }
                google.shared = "none";
                try { providers.Sheet(config).List(); Check(false, "a sheet that is not shared cannot be read"); }
                catch (GoogleException e) { Check(e.Message.Contains(Email) && e.Message.Contains("as an Editor"), "the refusal says whom to share the sheet with"); }
                google.shared = "writer";

                // ---- 새 시트: 묻는 말에 답해 가며 준비한다. 처음에는 공유를 빼먹었다 ----
                google.tabs.Clear();
                google.Add("Sheet1");
                google.Add("Notes", new[] { "memo" });
                google.shared = "none";
                string second = Path.Combine(folder, "second.json");
                var asked = new List<string>();
                said.Clear();
                string Answer(string question)
                {
                    asked.Add(question);
                    if (question.StartsWith("Which kind")) return "2"; // 1 CSV(코어), 2 Google Sheets(이 모듈)
                    if (question.StartsWith("Address")) return asked.FindAll(q => q.StartsWith("Address")).Count == 1 ? "not an address" : "https://docs.google.com/spreadsheets/d/" + Id + "/edit#gid=0";
                    if (question.StartsWith("Press Enter")) { google.shared = "writer"; return ""; }
                    if (question.StartsWith("Which tab")) return asked.FindAll(q => q.StartsWith("Which tab")).Count == 1 ? "9" : "1";
                    if (question.StartsWith("Language")) return "ko";
                    if (question.StartsWith("Folder")) return "";
                    throw new InvalidOperationException("unexpected question: " + question);
                }
                Check(SheetSetup.Run(new Args(new[] { "tc", "init", "--settings", second }), providers, Answer, said.Add) == 0, "init by answering questions");
                Check(said.Exists(line => line.StartsWith("using the Google key already on this PC")) && !asked.Exists(q => q.StartsWith("Path of the key")), "the key is asked for only once per PC");
                Check(said.Exists(line => line.Contains("press Share, add " + Email + " as an Editor")), "a sheet that is not shared yet: init says what to do and waits");
                Check(asked.FindAll(q => q.StartsWith("Address")).Count == 2 && asked.FindAll(q => q.StartsWith("Which tab")).Count == 2, "a wrong answer is asked again");
                Check(google.Cell("Sheet1", 0, 0) == "TC 번호" && google.Cell("Sheet1", 0, 2) == "기대 결과" && google.Cell("Sheet1", 0, 3) == "규칙 상태" && google.Cell("Notes", 0, 0) == "memo",
                    "an empty tab gets the column names; other tabs are left alone");
                Check(SheetConfig.Load(second, providers).Rules.Count == 0 && SheetConfig.Load(second, providers).Tc == "Sheet1", "the answers end up in the settings");

                // ---- 설정 화면이 부르는 명령: 키의 상태와 넣기, 시트의 탭과 칸 ----
                Console.SetOut(TextWriter.Null);
                try
                {
                    Check(new GoogleKeyCommand().Run(new Args(new[] { "tc", "key" })) == 0 && new GoogleKeyCommand().Run(new Args(new[] { "tc", "key", "--credentials", downloaded })) == 0,
                        "the key can be shown and replaced");
                    try { new GoogleKeyCommand().Run(new Args(new[] { "tc", "key", "--credentials", notKey })); Check(false, "a file that is not a key does not replace the key"); }
                    catch (UsageException) { Check(GoogleKey.Read(GoogleKey.DefaultFile).Email == Email, "the key that was there is kept"); }
                    var tabsArgs = new Args(new[] { "tc", "tabs", "--spreadsheet", Id });
                    Check(SheetSetup.TabsCommand(tabsArgs, providers) == 0, "a sheet that can be reached lists its tabs");
                    google.shared = "none";
                    Check(SheetSetup.TabsCommand(tabsArgs, providers) == 1, "a sheet that is not shared is reported, not thrown");
                    // 거절의 까닭은 Google이 붙인 reason으로 가린다. 안내 문구에 다른 까닭의 낱말이 있어도 속지 않는다(화면이 공유 문제를 API 문제로 안내했던 적이 있다)
                    SheetAccessException notShared = null, apiOff = null;
                    try { new GoogleProvider(google).Locate(new SetupContext(tabsArgs, question => null, line => { }, folder)); }
                    catch (SheetAccessException e) { notShared = e; }
                    google.shared = "writer";
                    google.apiOff = true;
                    try { new GoogleProvider(google).Locate(new SetupContext(tabsArgs, question => null, line => { }, folder)); }
                    catch (SheetAccessException e) { apiOff = e; }
                    google.apiOff = false;
                    Check(notShared?.Problem == "not_shared" && notShared.Hint.Contains("not shared with " + Email) && !notShared.Hint.Contains("Enable"),
                        "not shared: the hint says to share, and only that");
                    Check(apiOff?.Problem == "api_disabled" && apiOff.Hint.Contains("project=516115") && !apiOff.Hint.Contains("Share"),
                        "API off: the hint opens the page Google gave to turn it on");

                    // 화면이 고른 칸을 그대로 쓴다: 흔한 이름이 아니어도 된다
                    google.tabs.Clear();
                    google.Add("케이스", new[] { "코드", "절차서", "합격 기준" }, new[] { "A-1", "버튼을 누른다", "창이 닫힌다" });
                    string picked = Path.Combine(folder, "picked.json");
                    Check(SheetSetup.Run(new Args(new[]
                    {
                        "tc", "init", "--settings", picked, "--spreadsheet", Id, "--tab", "케이스", "--rules", Path.Combine(folder, "rules"),
                        "--tc-column", "코드", "--content-column", "절차서", "--expected-column", "합격 기준",
                    }), providers, question => null, said.Add) == 0, "init takes the columns the setup window picked");
                    SheetConfig pickedConfig = SheetConfig.Load(picked, providers);
                    Check(pickedConfig.Column("tc") == "코드" && pickedConfig.Column("expected") == "합격 기준" && pickedConfig.Summary == "요약"
                          && providers.Sheet(pickedConfig).List()[0].id == "A-1", "unusual column names work once they are picked");
                    try
                    {
                        SheetSetup.Run(new Args(new[] { "tc", "init", "--settings", Path.Combine(folder, "bad.json"), "--spreadsheet", Id, "--tc-column", "없는 칸" }), providers, question => null, said.Add);
                        Check(false, "a column that is not in the tab is refused");
                    }
                    catch (UsageException) { }
                }
                finally
                {
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                }

                // 아무도 답할 수 없는데 값이 모자라면 무엇을 줘야 하는지 말한다
                try { SheetSetup.Run(new Args(new[] { "tc", "init", "--settings", Path.Combine(folder, "third.json") }), providers, question => null, said.Add); Check(false, "init without answers needs options"); }
                catch (UsageException e) { Check(e.Message.Contains("--type csv|google"), "init says which option is missing"); }

                File.WriteAllText(settings, "{\"sheet\": {\"type\": \"google\", \"tc\": \"TC\"}}");
                try { SheetConfig.Load(settings, providers); Check(false, "a google sheet without an address is refused"); }
                catch (UsageException) { }
            }
            finally
            {
                Environment.SetEnvironmentVariable("QAPLAY_HOME", oldHome);
                Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", oldKey);
                Directory.Delete(folder, true);
            }
        }
    }
}
