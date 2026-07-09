using System;
using System.Threading;
using TVHeadEnd.Helper;
using TVHeadEnd.HTSP;

namespace TVHeadEnd.HTSP_Responses
{
    public class LoopBackResponseHandler : HTSResponseHandler
    {
        private readonly SizeQueue<HTSMessage> _responseDataQueue;

        public LoopBackResponseHandler()
        {
            _responseDataQueue = new SizeQueue<HTSMessage>(1);
        }

        public void handleResponse(HTSMessage response)
        {
            _responseDataQueue.Enqueue(response);
        }

        public HTSMessage getResponse()
        {
            return _responseDataQueue.Dequeue();
        }

        public HTSMessage getResponse(CancellationToken cancellationToken, TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero)
            {
                return getResponse();
            }

            var deadline = DateTime.UtcNow + timeout;
            while (!cancellationToken.IsCancellationRequested)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return null;
                }

                var waitTimeout = remaining < TimeSpan.FromMilliseconds(250)
                    ? remaining
                    : TimeSpan.FromMilliseconds(250);

                if (_responseDataQueue.TryDequeue(out HTSMessage response, cancellationToken, waitTimeout))
                {
                    return response;
                }
            }

            throw new OperationCanceledException(cancellationToken);
        }
    }
}
