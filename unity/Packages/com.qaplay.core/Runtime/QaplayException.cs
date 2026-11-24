using System;

namespace Qaplay
{
    /// <summary>명령이 실패했을 때 던진다. code는 protocol/README.md 3절의 오류 코드.</summary>
    public sealed class QaplayException : Exception
    {
        public readonly string code;
        /// <summary>not_tappable의 세부 이유(off_screen 등). 다른 오류에서는 null.</summary>
        public readonly string reason;

        /// <summary>ambiguous와 timeout에서 셀렉터에 걸린 요소 수. 다른 오류에서는 null.</summary>
        public readonly int? count;

        public QaplayException(string code, string message, string reason = null, int? count = null) : base(message)
        {
            this.code = code;
            this.reason = reason;
            this.count = count;
        }
    }
}
