using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Qaplay.Runner
{
    /// <summary>Google이 거절했을 때. Status는 HTTP 상태 코드다(403: 권한 없음, 404: 그런 시트 없음).</summary>
    sealed class GoogleException : IOException
    {
        public readonly int Status;

        public GoogleException(int status, string message) : base(message)
        {
            Status = status;
        }
    }

    /// <summary>
    /// Google 서비스 계정의 키(Google Cloud에서 내려받는 JSON 파일). 러너는 이 계정으로 시트를 읽고 쓴다.
    /// 시트는 이 계정의 메일 주소(Email)에 편집자로 공유되어 있어야 한다.
    /// </summary>
    sealed class GoogleKey
    {
        public string File, Email, PrivateKey, TokenUri;

        /// <summary>
        /// qaplay tc init이 키를 넣어 두는 곳: 사용자 폴더의 .qaplay. 저장소 밖이고 그 PC의 그 사용자 것이다.
        /// 환경 변수 QAPLAY_HOME으로 그 폴더를 바꿀 수 있다.
        /// </summary>
        public static string DefaultFile
        {
            get
            {
                string home = Environment.GetEnvironmentVariable("QAPLAY_HOME");
                if (string.IsNullOrEmpty(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".qaplay");
                return Path.Combine(home, "google-key.json");
            }
        }

        /// <summary>찾는 순서: 시트 설정의 credentials, 환경 변수 GOOGLE_APPLICATION_CREDENTIALS, 기본 위치.</summary>
        public static GoogleKey Find(string fromConfig)
        {
            string file = fromConfig ?? Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
            if (string.IsNullOrEmpty(file)) file = DefaultFile;
            if (!System.IO.File.Exists(file))
                throw new UsageException("no Google key file at " + file + ": run qaplay tc init, or point GOOGLE_APPLICATION_CREDENTIALS at the service account key");
            return Read(file);
        }

        public static GoogleKey Read(string file)
        {
            try
            {
                var json = (Dictionary<string, object>)Json.Parse(System.IO.File.ReadAllText(file));
                if (!"service_account".Equals(json.GetValueOrDefault("type")))
                    throw new FormatException("it is not a service account key (\"type\" is not service_account)");
                return new GoogleKey
                {
                    File = Path.GetFullPath(file),
                    Email = (string)json["client_email"], PrivateKey = (string)json["private_key"],
                    TokenUri = json.GetValueOrDefault("token_uri") as string ?? "https://oauth2.googleapis.com/token",
                };
            }
            catch (Exception e) when (e is FormatException || e is InvalidCastException || e is KeyNotFoundException)
            {
                // 파일의 내용은 메시지에 싣지 않는다(비밀 키가 들어 있다)
                throw new UsageException("cannot use " + file + " as a Google key: " + (e is FormatException ? e.Message : "client_email or private_key is missing"));
            }
        }
    }

    /// <summary>Google의 API를 부른다. 키로 서명한 요청으로 한 시간짜리 토큰을 받아 쓴다.</summary>
    sealed class GoogleApi
    {
        const string Sheets = "https://sheets.googleapis.com/v4/spreadsheets/";

        readonly GoogleKey m_Key;
        readonly HttpClient m_Http;
        string m_Token;

        /// <param name="handler">요청을 실제로 보내는 것. null이면 진짜 Google로 보낸다. 검사는 여기에 가짜를 넣는다.</param>
        public GoogleApi(GoogleKey key, HttpMessageHandler handler)
        {
            m_Key = key;
            m_Http = handler == null ? new HttpClient() : new HttpClient(handler, false);
            m_Http.Timeout = TimeSpan.FromSeconds(30);
        }

        public string Email => m_Key.Email;

        static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // 서비스 계정의 로그인: 키로 서명한 짧은 글(JWT)을 보내고 토큰을 받는다
        string Token()
        {
            if (m_Token != null) return m_Token;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string head = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
            string claims = Base64Url(Encoding.UTF8.GetBytes(Json.Write(new Dictionary<string, object>
            {
                ["iss"] = m_Key.Email, ["scope"] = "https://www.googleapis.com/auth/spreadsheets", ["aud"] = m_Key.TokenUri,
                ["iat"] = now, ["exp"] = now + 3600,
            })));
            byte[] signature;
            try
            {
                using RSA rsa = RSA.Create();
                rsa.ImportFromPem(m_Key.PrivateKey);
                signature = rsa.SignData(Encoding.ASCII.GetBytes(head + "." + claims), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            catch (Exception e) when (e is CryptographicException || e is ArgumentException)
            {
                throw new UsageException("the private key in " + m_Key.File + " cannot be used: download the key again");
            }
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer", ["assertion"] = head + "." + claims + "." + Base64Url(signature),
            });
            Dictionary<string, object> answer = Send(new HttpRequestMessage(HttpMethod.Post, m_Key.TokenUri) { Content = form });
            return m_Token = (string)answer["access_token"];
        }

        Dictionary<string, object> Send(HttpRequestMessage request)
        {
            HttpResponseMessage response;
            string text;
            try
            {
                response = m_Http.SendAsync(request).GetAwaiter().GetResult();
                text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException)
            {
                throw new IOException("cannot reach Google (" + request.RequestUri.Host + "): " + e.Message);
            }
            Dictionary<string, object> json;
            try
            {
                json = text.Length == 0 ? new Dictionary<string, object>() : (Dictionary<string, object>)Json.Parse(text);
            }
            catch (Exception e) when (e is FormatException || e is InvalidCastException)
            {
                throw new GoogleException((int)response.StatusCode, "Google answered " + (int)response.StatusCode + " with something that is not JSON");
            }
            if (response.IsSuccessStatusCode) return json;
            // 시트의 API는 {error: {message}}, 토큰을 주는 곳은 {error, error_description}으로 답한다
            string message = json.GetValueOrDefault("error") is Dictionary<string, object> error
                ? error.GetValueOrDefault("message") as string
                : json.GetValueOrDefault("error_description") as string ?? json.GetValueOrDefault("error") as string;
            throw new GoogleException((int)response.StatusCode, "Google answered " + (int)response.StatusCode + ": " + (message ?? "no message"));
        }

        Dictionary<string, object> Call(HttpMethod method, string url, object body = null)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token());
            if (body != null) request.Content = new StringContent(Json.Write(body), Encoding.UTF8, "application/json");
            return Send(request);
        }

        // 탭 이름을 범위로: 작은따옴표로 싸고, 이름 안의 작은따옴표는 둘로 적는다
        static string Quoted(string tab) => "'" + tab.Replace("'", "''") + "'";

        /// <summary>칸의 자리를 A1 꼴로. (0, 0)은 A1, (0, 26)은 AA1.</summary>
        public static string A1(int row, int column)
        {
            string letters = "";
            for (int n = column + 1; n > 0; n = (n - 1) / 26) letters = (char)('A' + (n - 1) % 26) + letters;
            return letters + (row + 1).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>탭마다 번호와 크기. 순서는 시트에서 보이는 순서다.</summary>
        public List<Dictionary<string, object>> Tabs(string spreadsheet)
        {
            Dictionary<string, object> answer = Call(HttpMethod.Get,
                Sheets + spreadsheet + "?fields=sheets.properties(sheetId,title,gridProperties(rowCount,columnCount))");
            var tabs = new List<Dictionary<string, object>>();
            foreach (object sheet in answer.GetValueOrDefault("sheets") as List<object> ?? new List<object>())
                tabs.Add((Dictionary<string, object>)((Dictionary<string, object>)sheet)["properties"]);
            return tabs;
        }

        /// <summary>
        /// 탭의 칸을 화면에 보이는 글자로. 뒤쪽의 빈 칸과 빈 줄은 오지 않는다.
        /// part를 주면 그 범위만 읽는다("1:1"은 첫 줄). 주지 않으면 탭 전체다.
        /// </summary>
        public List<List<string>> Values(string spreadsheet, string tab, string part = null)
        {
            Dictionary<string, object> answer = Call(HttpMethod.Get,
                Sheets + spreadsheet + "/values/" + Uri.EscapeDataString(Quoted(tab) + (part == null ? "" : "!" + part)) + "?valueRenderOption=FORMATTED_VALUE");
            var rows = new List<List<string>>();
            foreach (object row in answer.GetValueOrDefault("values") as List<object> ?? new List<object>())
            {
                var cells = new List<string>();
                foreach (object cell in (List<object>)row) cells.Add(Convert.ToString(cell, CultureInfo.InvariantCulture) ?? "");
                rows.Add(cells);
            }
            return rows;
        }

        /// <summary>탭을 만든다.</summary>
        public void AddTab(string spreadsheet, string tab) =>
            Call(HttpMethod.Post, Sheets + spreadsheet + ":batchUpdate", new Dictionary<string, object>
            {
                ["requests"] = new List<object> { Request("addSheet", "properties", new Dictionary<string, object> { ["title"] = tab }) },
            });

        /// <summary>탭의 줄이나 칸을 늘린다. dimension은 ROWS나 COLUMNS.</summary>
        public void Grow(string spreadsheet, object sheetId, string dimension, int by) =>
            Call(HttpMethod.Post, Sheets + spreadsheet + ":batchUpdate", new Dictionary<string, object>
            {
                ["requests"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["appendDimension"] = new Dictionary<string, object> { ["sheetId"] = sheetId, ["dimension"] = dimension, ["length"] = by },
                    },
                },
            });

        static object Request(string kind, string field, object value) =>
            new Dictionary<string, object> { [kind] = new Dictionary<string, object> { [field] = value } };

        /// <summary>칸들을 적는다. 값은 적은 그대로 들어간다(RAW): 글자는 글자로, 수는 수로. 수식으로 풀리지 않는다.</summary>
        public void Update(string spreadsheet, string tab, List<(int row, int column, object value)> cells)
        {
            if (cells.Count == 0) return;
            var data = new List<object>();
            // ponytail: 바뀐 칸마다 범위 하나. TC가 수천 줄이면 요청이 커진다. 그때는 이어진 칸을 한 범위로 묶는다
            foreach ((int row, int column, object value) in cells)
                data.Add(new Dictionary<string, object>
                {
                    ["range"] = Quoted(tab) + "!" + A1(row, column), ["values"] = new List<object> { new List<object> { value } },
                });
            Call(HttpMethod.Post, Sheets + spreadsheet + "/values:batchUpdate", new Dictionary<string, object> { ["valueInputOption"] = "RAW", ["data"] = data });
        }
    }

    /// <summary>
    /// Google Sheets의 시트 하나: TC 표와 요약 표가 탭으로 들어 있다. 쓸 때는 글자가 바뀐 칸만 고친다.
    /// 사람이 같은 시간에 다른 칸을 고치고 있어도 그 칸은 건드리지 않는다.
    /// </summary>
    sealed class GoogleSheet : SheetStore
    {
        readonly SheetConfig m_Config;
        GoogleApi m_Api;

        readonly HttpMessageHandler m_Handler;
        readonly string m_Id;

        /// <param name="handler">Google에 요청을 보내는 것. null이면 진짜 Google이다(검사할 때는 가짜를 넣는다).</param>
        public GoogleSheet(SheetConfig config, HttpMessageHandler handler)
        {
            m_Config = config;
            m_Handler = handler;
            m_Id = IdOf(config.Spreadsheet);
        }

        // 키는 처음 쓸 때 읽는다. 설정을 읽기만 하는 명령이 키 없이도 돌게 한다
        GoogleApi Api => m_Api ??= new GoogleApi(GoogleKey.Find(m_Config.Credentials == null ? null : m_Config.PathOf(m_Config.Credentials)), m_Handler);

        string Tab(string which) => which == "tc" ? m_Config.Tc : m_Config.Summary;

        public static string Address(string spreadsheet) => "https://docs.google.com/spreadsheets/d/" + spreadsheet;

        /// <summary>주소나 ID에서 시트의 ID를 꺼낸다. 주소는 브라우저의 주소 칸에 있는 그대로다.</summary>
        public static string IdOf(string text)
        {
            Match match = Regex.Match(text.Trim(), "/spreadsheets/d/([A-Za-z0-9_-]+)");
            if (match.Success) return match.Groups[1].Value;
            if (Regex.IsMatch(text.Trim(), "^[A-Za-z0-9_-]{20,}$")) return text.Trim();
            throw new UsageException("not a Google Sheets address: " + text + " (it looks like https://docs.google.com/spreadsheets/d/.../edit)");
        }

        public override string Where(string which) => Tab(which) == null ? null : Address(m_Id) + " [" + Tab(which) + "]";

        // 권한이 없다는 답에는 무엇을 하면 되는지를 붙인다. 가장 흔한 실수가 공유를 빼먹는 것이다
        T Guided<T>(Func<T> call)
        {
            try
            {
                return call();
            }
            catch (GoogleException e) when (e.Status == 403 || e.Status == 404)
            {
                throw new GoogleException(e.Status, e.Message + " (" + Hint(e.Status, Api.Email, m_Id) + ")");
            }
        }

        public static string Hint(int status, string email, string spreadsheet) =>
            status == 404
                ? "there is no sheet at " + Address(spreadsheet) + ": copy the address again from the browser"
                : "open " + Address(spreadsheet) + ", press Share and add " + email + " as an Editor; if the message says the API is disabled, enable the Google Sheets API for the key's project";

        Dictionary<string, object> Find(string tab) => Guided(() => Api.Tabs(m_Id)).Find(t => tab.Equals(t["title"]));

        public override List<List<string>> Read(string which)
        {
            if (Tab(which) == null || Find(Tab(which)) == null) return new List<List<string>>();
            return Api.Values(m_Id, Tab(which));
        }

        public override void Write(string which, List<List<string>> rows)
        {
            string tab = Tab(which);
            Dictionary<string, object> found = Find(tab);
            if (found == null)
            {
                Api.AddTab(m_Id, tab);
                found = Find(tab);
            }
            List<List<string>> now = Api.Values(m_Id, tab);
            var changed = new List<(int, int, object)>();
            int columns = 0;
            for (int r = 0; r < rows.Count; r++)
            {
                columns = Math.Max(columns, rows[r].Count);
                for (int c = 0; c < rows[r].Count; c++)
                {
                    string text = rows[r][c] ?? "", before = r < now.Count && c < now[r].Count ? now[r][c] : "";
                    if (text == before) continue;
                    // 개수 같은 것은 시트에서 셈할 수 있게 수로 적는다. 0으로 시작하는 글자(번호)는 글자로 둔다
                    bool number = text.Length > 0 && text.Length < 10 && (text == "0" || text[0] != '0')
                                  && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value);
                    changed.Add((r, c, number ? int.Parse(text, CultureInfo.InvariantCulture) : (object)text));
                }
            }
            // 탭의 크기를 넘는 자리에는 쓸 수 없다. 모자라면 먼저 늘린다
            var grid = (Dictionary<string, object>)found["gridProperties"];
            int haveRows = Convert.ToInt32(grid["rowCount"], CultureInfo.InvariantCulture), haveColumns = Convert.ToInt32(grid["columnCount"], CultureInfo.InvariantCulture);
            // 보기 권한만 있으면 여기서 거절된다. 편집자로 공유하라는 안내를 붙인다
            Guided(() =>
            {
                if (rows.Count > haveRows) Api.Grow(m_Id, found["sheetId"], "ROWS", rows.Count - haveRows);
                if (columns > haveColumns) Api.Grow(m_Id, found["sheetId"], "COLUMNS", columns - haveColumns);
                Api.Update(m_Id, tab, changed);
                return true;
            });
        }
    }

    /// <summary>Google Sheets: 시트 하나에 TC 표와 요약 표가 탭으로 들어 있다. 서비스 계정의 키로 로그인한다.</summary>
    sealed class GoogleProvider : ISheetProvider
    {
        const string KeySteps =
            "A Google service account key is needed, once per PC. To get one:\n" +
            "  1. Open https://console.cloud.google.com/ and create a project (or pick one).\n" +
            "  2. APIs & Services > Library > search \"Google Sheets API\" > Enable.\n" +
            "  3. IAM & Admin > Service Accounts > Create service account. Any name; it needs no roles.\n" +
            "  4. Open that account > Keys > Add key > Create new key > JSON. A .json file is downloaded.\n" +
            "The same steps with more detail are in docs/sheet.md. In Unity the window qaplay > Sheet walks you through them.";

        readonly HttpMessageHandler m_Handler;

        /// <param name="handler">Google에 요청을 보내는 것. null이면 진짜 Google이다.</param>
        public GoogleProvider(HttpMessageHandler handler)
        {
            m_Handler = handler;
        }

        public string Type => "google";
        public string Name => "Google Sheets";

        public void Check(SheetConfig config)
        {
            if (config.Spreadsheet == null) throw new FormatException("a google sheet needs \"spreadsheet\": its address");
            try
            {
                GoogleSheet.IdOf(config.Spreadsheet);
            }
            catch (UsageException e)
            {
                throw new FormatException(e.Message);
            }
            if (config.Tc == null) throw new FormatException("\"tc\" is missing: the name of the tab with the test cases");
        }

        public SheetStore Open(SheetConfig config) => new GoogleSheet(config, m_Handler);

        public string SummaryName(string tc, string language) => language == "ko" ? "요약" : "Summary";

        /// <summary>지금 쓰게 될 키. 없으면 null. 찾는 순서는 러너가 시트를 쓸 때와 같다.</summary>
        public static GoogleKey Installed()
        {
            string fromEnv = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
            string file = !string.IsNullOrEmpty(fromEnv) && System.IO.File.Exists(fromEnv) ? fromEnv : GoogleKey.DefaultFile;
            return System.IO.File.Exists(file) ? GoogleKey.Read(file) : null;
        }

        /// <summary>그 파일을 이 PC의 키로 넣는다(사용자 폴더, 저장소 밖). 키가 아니면 있던 키를 건드리지 않고 거절한다.</summary>
        public static GoogleKey Install(string file)
        {
            if (!System.IO.File.Exists(file)) throw new UsageException("no such file: " + file);
            GoogleKey.Read(file);
            if (!Path.GetFullPath(file).Equals(Path.GetFullPath(GoogleKey.DefaultFile), StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(GoogleKey.DefaultFile));
                System.IO.File.Copy(file, GoogleKey.DefaultFile, true);
            }
            return GoogleKey.Read(GoogleKey.DefaultFile);
        }

        // 키를 찾거나 받는다. 한 번 넣어 두면 그 PC에서는 다시 묻지 않는다
        static GoogleKey Key(SetupContext context)
        {
            string given = context.args.Get("credentials", null);
            if (given != null) return Install(given);
            GoogleKey installed = Installed();
            if (installed != null)
            {
                context.say("using the Google key already on this PC: " + installed.File);
                return installed;
            }
            context.say(KeySteps);
            while (true)
            {
                string answer = context.ask("Path of the key file you downloaded (.json)")
                                ?? throw new UsageException("nobody is here to answer: give --credentials FILE");
                try
                {
                    GoogleKey key = Install(answer.Trim().Trim('"'));
                    context.say("key saved to " + GoogleKey.DefaultFile + " (outside the project; the downloaded file is no longer needed)");
                    return key;
                }
                catch (UsageException e)
                {
                    context.say(e.Message);
                }
            }
        }

        public SheetSource Locate(SetupContext context)
        {
            GoogleKey key = Key(context);
            var api = new GoogleApi(key, m_Handler);
            context.say("");
            context.say("The runner signs in to Google as: " + key.Email);
            context.say("Share your sheet with that address as an Editor (the Share button of the sheet), if you have not yet.");

            // 닿을 때까지 무엇이 모자란지 알려 준다. 답할 사람이 없으면 까닭과 할 일을 담아 끝낸다
            string given = context.args.Get("spreadsheet", null), id = null;
            List<Dictionary<string, object>> tabs = null;
            while (tabs == null)
            {
                string text = given ?? context.ask("Address of the Google Sheet (copy it from the browser's address bar)")
                              ?? throw new UsageException("nobody is here to answer: give --spreadsheet ADDRESS");
                try
                {
                    id = GoogleSheet.IdOf(text);
                    tabs = api.Tabs(id);
                }
                catch (UsageException e) when (given == null)
                {
                    context.say(e.Message); // 주소가 아닌 것을 적었다: 다시 묻는다
                }
                catch (GoogleException e) when (e.Status == 403 || e.Status == 404)
                {
                    string hint = GoogleSheet.Hint(e.Status, key.Email, id);
                    context.say(e.Message);
                    context.say(hint);
                    if (e.Status == 404 && given == null) continue;
                    // 공유하고 나서 이어 간다. 주소는 다시 묻지 않는다
                    given = text;
                    string answer = context.ask("Press Enter when that is done (or type q to stop)");
                    if (answer == null || answer.Trim() == "q") throw new SheetAccessException(e.Status, e.Message, hint, key.Email);
                }
            }

            var source = new SheetSource { account = key.Email };
            source.settings["spreadsheet"] = GoogleSheet.Address(id);
            foreach (Dictionary<string, object> tab in tabs)
            {
                var table = new SheetTable { name = (string)tab["title"] };
                List<List<string>> first = api.Values(id, table.name, "1:1");
                if (first.Count > 0) table.header = first[0];
                source.tables.Add(table);
            }
            return source;
        }
    }
}
