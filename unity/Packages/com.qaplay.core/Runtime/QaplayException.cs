using System;

namespace Qaplay
{
    /// <summary>명령이 실패했을 때 던진다. code는 protocol/README.md 3절의 오류 코드.</summary>
    public sealed class QaplayException : Exception
    {
        public readonly string code;

        public QaplayException(string code, string message) : base(message)
        {
            this.code = code;
        }
    }
}
