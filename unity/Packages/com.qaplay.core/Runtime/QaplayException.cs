using System;

namespace Qaplay
{
    /// <summary>명령이 실패했을 때 던진다. code는 protocol/README.md 3절의 오류 코드.</summary>
    public sealed class QaplayException : Exception
    {
        public readonly string code;
        /// <summary>not_tappable의 세부 이유(off_screen 등). 다른 오류에서는 null.</summary>
        public readonly string reason;

        public QaplayException(string code, string message, string reason = null) : base(message)
        {
            this.code = code;
            this.reason = reason;
        }
    }
}
