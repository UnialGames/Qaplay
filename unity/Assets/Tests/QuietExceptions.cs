using System;
using Qaplay.Agent;
using UnityEngine;

/// <summary>
/// 테스트가 일부러 내는 예외를 Console에 빨갛게 남기지 않는다.
/// 켜져 있는 동안, 메시지가 expected에 걸리는 예외는 Unity의 로그로 보내지 않고 에이전트에 바로 알린다
/// (Unity가 로그 콜백에 넘기는 것과 같은 인자로). 그 밖의 로그는 그대로 Unity로 간다.
/// Unity의 로그를 실제로 거치는 길은 Console이 없는 배치 모드에서만 따로 확인한다(ServerTests).
/// </summary>
public sealed class QuietExceptions : ILogHandler, IDisposable
{
    readonly ILogHandler m_Unity;
    readonly AgentServer m_Server;
    readonly string[] m_Expected;

    /// <summary>지금까지 삼킨 예외 수.</summary>
    public int Count { get; private set; }

    public QuietExceptions(AgentServer server, params string[] expected)
    {
        m_Server = server;
        m_Expected = expected;
        m_Unity = Debug.unityLogger.logHandler;
        Debug.unityLogger.logHandler = this;
    }

    public void LogException(Exception exception, UnityEngine.Object context)
    {
        if (!Array.Exists(m_Expected, part => exception.Message.Contains(part)))
        {
            m_Unity.LogException(exception, context);
            return;
        }
        Count++;
        m_Server.OnLog(exception.GetType().Name + ": " + exception.Message, exception.StackTrace ?? "", LogType.Exception);
    }

    public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args) =>
        m_Unity.LogFormat(logType, context, format, args);

    public void Dispose() => Debug.unityLogger.logHandler = m_Unity;
}
