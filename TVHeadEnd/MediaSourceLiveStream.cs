using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;

namespace TVHeadEnd
{
    public class MediaSourceLiveStream : ILiveStream
    {
        private readonly Func<Task> _close;

        public MediaSourceLiveStream(MediaSourceInfo mediaSource, Func<Task> close)
        {
            MediaSource = mediaSource;
            _close = close;
            ConsumerCount = 1;
            UniqueId = Guid.NewGuid().ToString("N");
            EnableStreamSharing = false;
        }

        public int ConsumerCount { get; set; }

        public string OriginalStreamId { get; set; }

        public string TunerHostId => null;

        public bool EnableStreamSharing { get; set; }

        public MediaSourceInfo MediaSource { get; set; }

        public string UniqueId { get; }

        public Task Open(CancellationToken openCancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task Close()
        {
            return _close();
        }

        public Stream GetStream()
        {
            throw new NotSupportedException();
        }

        public void Dispose()
        {
        }
    }
}
