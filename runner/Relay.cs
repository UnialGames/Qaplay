using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Qaplay.Runner
{
    /// <summary>
    /// Python 스크립트와 게임 사이의 중계. 스크립트는 러너의 localhost 포트에 게임과 같은 프로토콜로 접속한다.
    /// </summary>
    // ponytail: 바이트를 그대로 양쪽으로 옮긴다. 메시지를 들여다보지 않는다.
    // 러너가 크래시 복구나 기기 로그를 맡는 4단계에서 프레임 단위 중계로 바꾼다.
    static class Relay
    {
        /// <summary>
        /// command가 있으면 그 명령을 실행하고 끝나면 그 종료 코드로 끝난다(접속할 포트는 환경 변수 QAPLAY_RUNNER_PORT).
        /// 없으면 멈출 때까지 중계만 한다.
        /// </summary>
        public static int Run(int listenPort, string agentHost, int agentPort, List<string> command)
        {
            var listener = new TcpListener(IPAddress.Loopback, listenPort);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port; // 0을 주면 운영체제가 고른 번호
            new Thread(() => AcceptLoop(listener, agentHost, agentPort)) { IsBackground = true }.Start();

            if (command.Count == 0)
            {
                Console.WriteLine("relaying 127.0.0.1:" + port + " -> " + agentHost + ":" + agentPort + " (Ctrl+C to stop)");
                Thread.Sleep(Timeout.Infinite);
            }

            var start = new ProcessStartInfo(command[0]) { UseShellExecute = false };
            for (int i = 1; i < command.Count; i++) start.ArgumentList.Add(command[i]);
            start.Environment["QAPLAY_RUNNER_PORT"] = port.ToString(CultureInfo.InvariantCulture);
            try
            {
                using Process process = Process.Start(start);
                process.WaitForExit();
                return process.ExitCode;
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                throw new UsageException("cannot run " + command[0] + ": " + e.Message);
            }
        }

        static void AcceptLoop(TcpListener listener, string agentHost, int agentPort)
        {
            while (true)
            {
                TcpClient script = listener.AcceptTcpClient();
                new Thread(() => Pipe(script, agentHost, agentPort)) { IsBackground = true }.Start();
            }
        }

        static void Pipe(TcpClient script, string agentHost, int agentPort)
        {
            var agent = new TcpClient();
            try
            {
                agent.Connect(agentHost, agentPort);
            }
            catch (SocketException e)
            {
                Console.Error.WriteLine("qaplay: cannot reach the game at " + agentHost + ":" + agentPort + ": " + e.Message);
                script.Close();
                return;
            }
            script.NoDelay = agent.NoDelay = true;
            new Thread(() => Copy(script, agent)) { IsBackground = true }.Start();
            Copy(agent, script);
        }

        // 한쪽이 끊기면 양쪽을 닫는다
        static void Copy(TcpClient from, TcpClient to)
        {
            try
            {
                from.GetStream().CopyTo(to.GetStream());
            }
            catch (Exception)
            {
                // 끊긴 연결
            }
            finally
            {
                from.Close();
                to.Close();
            }
        }
    }
}
