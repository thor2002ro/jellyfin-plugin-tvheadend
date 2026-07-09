using System;
using System.Threading;
using TVHeadEnd.Helper;

namespace TVHeadEnd.HTSP.Responses
{
    public class LoopBackResponseHandler : IHTSResponseHandler
    {
        private readonly BlockingBuffer<HTSMessage> _responseDataQueue;

        public LoopBackResponseHandler()
        {
            _responseDataQueue = new BlockingBuffer<HTSMessage>(1);
        }

        public void HandleResponse(HTSMessage response)
        {
            _responseDataQueue.Enqueue(response);
        }

        public HTSMessage GetResponse()
        {
            return _responseDataQueue.Dequeue();
        }

        public HTSMessage GetResponse(CancellationToken cancellationToken, TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero)
            {
                return GetResponse();
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
