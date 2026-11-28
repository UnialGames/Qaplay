using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Qaplay.Runner
{
    /// <summary>
    /// qaplay dashboard: 실행 리포트를 브라우저로 보여 주는 작은 웹 서버. QA PC에서 띄워 둔다.
    /// 읽기만 한다(리포트를 고치거나 지우는 것은 qaplay reports 명령이 한다). 요청마다 폴더를 다시 읽으므로 새 실행이 바로 보인다.
    /// </summary>
    static class Dashboard
    {
        public const int DefaultPort = 27280;
        /// <summary>추이에 쓰는 최근 실행의 수.</summary>
        const int TrendRuns = 50;

        // 추이로 그리는 측정값: 규칙 결과의 어느 묶음의 어느 값인지와 단위
        static readonly string[][] Metrics =
        {
            new[] { "frames", "averageMs", "ms" }, new[] { "frames", "p99Ms", "ms" }, new[] { "frames", "hitches", "" },
            new[] { "growth", "managed", "MB/repeat" }, new[] { "growth", "native", "MB/repeat" },
            new[] { "device", "cpu", "%" }, new[] { "device", "pss", "MB" }, new[] { "device", "batteryTemp", "C" },
        };

        public sealed class Reply
        {
            public int status = 200;
            public string type = "text/plain; charset=utf-8";
            public byte[] body;
        }

        public static int Run(Args args)
        {
            string root = ReportStore.From(args).Root;
            IPEndPoint where = Endpoint(args.Get("listen", DefaultPort.ToString(CultureInfo.InvariantCulture)));
            TcpListener listener = Start(root, where);
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Console.WriteLine("dashboard: http://localhost:" + port + "/");
            Console.WriteLine("showing the runs in " + root);
            if (!IPAddress.IsLoopback(where.Address))
                Console.WriteLine("other PCs on this network can open http://" + Dns.GetHostName() + ":" + port + "/ (there is no login: anyone who can reach this PC can read the reports)");
            Console.WriteLine("press Ctrl+C to stop");
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        /// <summary>"27280"은 이 PC에서만(127.0.0.1), "0.0.0.0:27280"처럼 주소를 적으면 그 주소에서 받는다.</summary>
        public static IPEndPoint Endpoint(string listen)
        {
            if (int.TryParse(listen, NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port <= 65535) return new IPEndPoint(IPAddress.Loopback, port);
            if (listen.Contains(":") && IPEndPoint.TryParse(listen, out IPEndPoint parsed)) return parsed;
            throw new UsageException("--listen must be PORT (this PC only) or ADDRESS:PORT (for example 0.0.0.0:" + DefaultPort + " to let other PCs in)");
        }

        /// <summary>듣기 시작하고 바로 돌아온다. 받은 연결은 뒤에서 처리한다. 돌려준 것을 Stop하면 끝난다.</summary>
        public static TcpListener Start(string root, IPEndPoint where)
        {
            var listener = new TcpListener(where);
            try
            {
                listener.Start();
            }
            catch (SocketException e)
            {
                throw new IOException("cannot listen on " + where + ": " + e.Message + " (another dashboard may be running; pick another port with --listen)");
            }
            new Thread(() =>
            {
                while (true)
                {
                    TcpClient client;
                    try
                    {
                        client = listener.AcceptTcpClient();
                    }
                    catch (Exception e) when (e is SocketException || e is ObjectDisposedException || e is InvalidOperationException)
                    {
                        return; // 멈췄다
                    }
                    ThreadPool.QueueUserWorkItem(_ => Serve(client, root));
                }
            }) { IsBackground = true }.Start();
            return listener;
        }

        // 연결 하나에 요청 하나. 머리말만 읽는다(GET에는 본문이 없다)
        static void Serve(TcpClient client, string root)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = client.SendTimeout = 10000;
                    NetworkStream stream = client.GetStream();
                    var head = new StringBuilder();
                    var buffer = new byte[4096];
                    while (head.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0)
                    {
                        int read = stream.Read(buffer, 0, buffer.Length);
                        if (read <= 0 || head.Length > 16384) return;
                        head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }
                    string[] lines = head.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
                    string[] request = lines[0].Split(' ');
                    if (request.Length < 2) return;
                    string host = "";
                    foreach (string line in lines)
                        if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) host = line.Substring(5).Trim();

                    Reply reply;
                    try
                    {
                        reply = Handle(request[0], request[1], host, root);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is UsageException || e is FormatException)
                    {
                        reply = Text(500, e.Message);
                    }
                    byte[] top = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 " + reply.status + " " + (reply.status == 200 ? "OK" : "Error") + "\r\n" +
                        "Content-Type: " + reply.type + "\r\n" +
                        "Content-Length: " + reply.body.Length + "\r\n" +
                        // 올린 파일(로그, 화면)을 브라우저가 다른 종류로 짐작해 실행하지 않게 한다. 리포트는 늘 새로 읽는다
                        "X-Content-Type-Options: nosniff\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                    stream.Write(top, 0, top.Length);
                    stream.Write(reply.body, 0, reply.body.Length);
                }
                catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException)
                {
                    // 브라우저가 먼저 끊었다
                }
            }
        }

        /// <summary>요청 하나에 답한다. 서버 없이도 부를 수 있다(selftest).</summary>
        public static Reply Handle(string method, string target, string host, string root)
        {
            if (method != "GET") return Text(405, "only GET");
            // 다른 사이트가 자기 이름을 이 PC의 주소로 바꿔 리포트를 읽어 가는 것(DNS rebinding)을 막는다: 주소로 쓴 이름이 이 PC의 것이어야 한다
            if (!OwnName(host)) return Text(403, "open the dashboard by localhost, this PC's name or its IP address");

            int query = target.IndexOf('?');
            string[] parts = (query < 0 ? target : target.Substring(0, query)).Trim('/').Split('/');
            for (int n = 0; n < parts.Length; n++) parts[n] = Uri.UnescapeDataString(parts[n]);
            var store = new ReportStore(root);

            if (parts.Length == 1 && parts[0] == "") return new Reply { type = "text/html; charset=utf-8", body = Page() };
            if (parts.Length == 2 && parts[0] == "api" && parts[1] == "runs")
            {
                List<Dictionary<string, object>> runs = store.All();
                runs.Reverse();
                return Data(new Dictionary<string, object>
                {
                    ["folder"] = store.Root, ["runs"] = runs.ConvertAll(ReportCommands.Brief), ["unreadable"] = store.Unreadable,
                });
            }
            if (parts.Length == 2 && parts[0] == "api" && parts[1] == "trends") return Data(Trends(store));
            if (parts.Length == 3 && parts[0] == "api" && parts[1] == "runs" && Plain(parts[2]))
            {
                Dictionary<string, object> run = store.All().Find(r => parts[2].Equals(r["id"]));
                if (run == null) return Text(404, "no such run");
                // 기한이 지나 지워진 증거는 그렇다고 알려 준다
                string folder = store.FolderOf(parts[2]);
                void Mark(object evidence)
                {
                    foreach (object entry in (List<object>)evidence)
                    {
                        var item = (Dictionary<string, object>)entry;
                        item["file"] = Path.GetFileName((string)item["file"]);
                        item["removed"] = !File.Exists(Path.Combine(folder, (string)item["file"]));
                    }
                }
                foreach (object rule in (List<object>)run["rules"]) Mark(((Dictionary<string, object>)rule)["evidence"]);
                Mark(run["evidence"]);
                return Data(run);
            }
            if (parts.Length == 3 && parts[0] == "files" && Plain(parts[1]) && Plain(parts[2]))
            {
                // 실행의 폴더 바로 안의 파일만 준다. 이름에 경로가 섞인 것은 Plain에서 걸러졌다
                string folder = store.FolderOf(parts[1]), file = Path.Combine(folder, parts[2]);
                if (!File.Exists(Path.Combine(folder, "run.json")) || !File.Exists(file)) return Text(404, "no such file");
                string extension = Path.GetExtension(file).ToLowerInvariant();
                return new Reply { type = extension == ".png" ? "image/png" : "text/plain; charset=utf-8", body = File.ReadAllBytes(file) };
            }
            return Text(404, "not found");
        }

        // 폴더나 파일 하나의 이름인지: 경로 구분자, 드라이브, 위로 올라가는 이름이 없다
        static bool Plain(string name) =>
            name.Length > 0 && name != "." && name != ".." && name.IndexOfAny(new[] { '/', '\\', ':' }) < 0 && Path.GetFileName(name) == name;

        static bool OwnName(string host)
        {
            string name = host.StartsWith("[", StringComparison.Ordinal)
                ? host.Substring(1, Math.Max(0, host.IndexOf(']') - 1))
                : host.Split(':')[0];
            return name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(name, out _)
                   || name.Equals(Dns.GetHostName(), StringComparison.OrdinalIgnoreCase);
        }

        static Reply Text(int status, string text) => new Reply { status = status, body = Encoding.UTF8.GetBytes(text + "\n") };

        static Reply Data(object data) => new Reply { type = "application/json; charset=utf-8", body = Encoding.UTF8.GetBytes(Json.Write(data)) };

        static byte[] Page()
        {
            using Stream stream = typeof(Dashboard).Assembly.GetManifestResourceStream("Qaplay.Runner.Dashboard.html");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }

        /// <summary>
        /// 최근 실행들에서 규칙마다의 측정값을 시간 순서로 모은다. 같은 기기, 같은 규칙 파일, 같은 값끼리 한 줄이다
        /// (기기가 다르면 프레임 시간이나 메모리를 견줄 수 없다).
        /// </summary>
        public static Dictionary<string, object> Trends(ReportStore store)
        {
            List<Dictionary<string, object>> runs = store.All();
            if (runs.Count > TrendRuns) runs = runs.GetRange(runs.Count - TrendRuns, TrendRuns);
            var series = new List<object>();
            var byKey = new Dictionary<string, List<object>>();
            foreach (Dictionary<string, object> run in runs)
                foreach (object entry in (List<object>)run["rules"])
                {
                    var rule = (Dictionary<string, object>)entry;
                    foreach (string[] metric in Metrics)
                    {
                        if (!(rule.GetValueOrDefault(metric[0]) is Dictionary<string, object> group) || group.GetValueOrDefault(metric[1]) == null) continue;
                        string key = run["device"] + "\n" + rule["file"] + "\n" + metric[0] + "." + metric[1];
                        if (!byKey.TryGetValue(key, out List<object> points))
                        {
                            byKey[key] = points = new List<object>();
                            series.Add(new Dictionary<string, object>
                            {
                                ["device"] = run["device"], ["file"] = rule["file"], ["name"] = rule.GetValueOrDefault("name"),
                                ["metric"] = metric[0] + "." + metric[1], ["unit"] = metric[2], ["points"] = points,
                            });
                        }
                        points.Add(new Dictionary<string, object>
                        {
                            ["run"] = run["id"], ["time"] = run["time"], ["build"] = run["build"],
                            ["value"] = Convert.ToDouble(group[metric[1]], CultureInfo.InvariantCulture),
                        });
                    }
                }
            return new Dictionary<string, object> { ["runs"] = runs.Count, ["series"] = series };
        }
    }
}
