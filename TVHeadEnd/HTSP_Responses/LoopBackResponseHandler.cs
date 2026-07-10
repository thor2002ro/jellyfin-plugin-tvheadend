using System;
using System.Threading;
using System.Threading.Tasks;
using TVHeadEnd.HTSP;

namespace TVHeadEnd.HTSP_Responses
{
    public class LoopBackResponseHandler : HTSResponseHandler
    {
        private readonly TaskCompletionSource<HTSMessage> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void handleResponse(HTSMessage response)
        {
            _response.TrySetResult(response);
        }

        public Task<HTSMessage> GetResponseAsync(CancellationToken cancellationToken, TimeSpan timeout)
        {
            return timeout <= TimeSpan.Zero
                ? _response.Task.WaitAsync(cancellationToken)
                : _response.Task.WaitAsync(timeout, cancellationToken);
        }
    }
}
