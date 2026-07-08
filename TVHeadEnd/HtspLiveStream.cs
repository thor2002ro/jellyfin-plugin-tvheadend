using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;
using TVHeadEnd.HTSP;

namespace TVHeadEnd
{
    public class HtspLiveStream : ILiveStream, IDirectStreamProvider, HTSConnectionListener
    {
        private static int _nextSubscriptionId;

        private readonly string _channelId;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IServerApplicationHost _appHost;
        private readonly ILogger<HtspLiveStream> _logger;
        private readonly BlockingByteStream _stream = new BlockingByteStream();
        private readonly HtspTransportStreamMuxer _muxer = new HtspTransportStreamMuxer();
        private readonly TaskCompletionSource<bool> _firstPacket = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private HTSConnectionAsync _connection;
        private int _subscriptionId;
        private bool _closing;

        public HtspLiveStream(MediaSourceInfo mediaSource, string channelId, ILoggerFactory loggerFactory, IServerApplicationHost appHost)
        {
            MediaSource = mediaSource;
            _channelId = channelId;
            _loggerFactory = loggerFactory;
            _appHost = appHost;
            _logger = loggerFactory.CreateLogger<HtspLiveStream>();
            UniqueId = Guid.NewGuid().ToString("N");
            ConsumerCount = 1;
            EnableStreamSharing = false;
        }

        public int ConsumerCount { get; set; }

        public string OriginalStreamId { get; set; }

        public string TunerHostId => null;

        public bool EnableStreamSharing { get; set; }

        public MediaSourceInfo MediaSource { get; set; }

        public string UniqueId { get; }

        public async Task Open(CancellationToken openCancellationToken)
        {
            var config = Plugin.Instance.Configuration;
            _subscriptionId = Interlocked.Increment(ref _nextSubscriptionId);
            _connection = new HTSConnectionAsync(this, "TVHclient4Jellyfin-HTSP", "" + HTSMessage.HTSP_VERSION, _loggerFactory);

            try
            {
                _connection.open(config.TVH_ServerName.Trim(), config.HTSP_Port);
                if (!_connection.authenticate(config.Username.Trim(), config.Password.Trim()))
                {
                    throw new InvalidOperationException("TVHeadend HTSP authentication failed.");
                }

                var subscribe = new HTSMessage { Method = "subscribe" };
                subscribe.putField("channelId", Convert.ToInt32(_channelId));
                subscribe.putField("subscriptionId", _subscriptionId);
                subscribe.putField("weight", 100);
                subscribe.putField("normts", 1);

                var response = new TaskResponseHandler();
                _connection.sendMessage(subscribe, response);
                await response.Task.WaitAsync(openCancellationToken).ConfigureAwait(false);

                var firstPacket = await Task.WhenAny(_firstPacket.Task, Task.Delay(TimeSpan.FromSeconds(10), openCancellationToken)).ConfigureAwait(false);
                if (firstPacket != _firstPacket.Task)
                {
                    throw new TimeoutException("Timed out waiting for the first HTSP mux packet.");
                }

                await _firstPacket.Task.ConfigureAwait(false);
                MediaSource.Path = _appHost.GetApiUrlForLocalAccess() + "/LiveTv/LiveStreamFiles/" + UniqueId + "/stream.ts";
                MediaSource.Protocol = MediaProtocol.Http;
            }
            catch
            {
                await Close().ConfigureAwait(false);
                throw;
            }
        }

        public Task Close()
        {
            _closing = true;
            EnableStreamSharing = false;
            _stream.Complete();

            if (_connection != null)
            {
                try
                {
                    var unsubscribe = new HTSMessage { Method = "unsubscribe" };
                    unsubscribe.putField("subscriptionId", _subscriptionId);
                    var response = new TaskResponseHandler();
                    _connection.sendMessage(unsubscribe, response);
                    response.Task.Wait(TimeSpan.FromSeconds(2));
                }
                catch
                {
                }

                _connection.stop();
                _connection = null;
            }

            return Task.CompletedTask;
        }

        public Stream GetStream()
        {
            return _stream;
        }

        public void onError(Exception ex)
        {
            if (_closing)
            {
                _logger.LogDebug(ex, "HTSP live stream connection closed");
                _firstPacket.TrySetCanceled();
                _stream.Complete();
                return;
            }

            _logger.LogError(ex, "HTSP live stream error");
            _firstPacket.TrySetException(ex);
            _stream.Complete(ex);
        }

        public void onMessage(HTSMessage response)
        {
            if (response == null)
            {
                return;
            }

            if (response.Method == "subscriptionStart")
            {
                ParseSubscriptionStart(response);
                return;
            }

            if (response.Method != "muxpkt" || !response.containsField("payload"))
            {
                if (response.Method == "subscriptionStop")
                {
                    _stream.Complete();
                }

                return;
            }

            if (response.containsField("subscriptionId") && response.getInt("subscriptionId") != _subscriptionId)
            {
                return;
            }

            if (!response.containsField("stream"))
            {
                return;
            }

            var payload = response.getByteArray("payload");
            if (LooksLikeTransportStream(payload))
            {
                _stream.WriteChunk(payload);
                _firstPacket.TrySetResult(true);
                return;
            }

            if (!_muxer.HasStreams)
            {
                var ex = new InvalidOperationException("HTSP muxpkt payload is not raw MPEG-TS and no subscriptionStart stream metadata was received for muxing.");
                _firstPacket.TrySetException(ex);
                _stream.Complete(ex);
                return;
            }

            var chunk = _muxer.WritePacket(
                response.getInt("stream"),
                payload,
                response.containsField("pts") ? response.getLong("pts") : null,
                response.containsField("dts") ? response.getLong("dts") : null);

            if (chunk.Length > 0)
            {
                _stream.WriteChunk(chunk);
                _firstPacket.TrySetResult(true);
            }
        }

        private void ParseSubscriptionStart(HTSMessage response)
        {
            if (!response.containsField("streams"))
            {
                return;
            }

            var streams = new List<HtspTransportStreamMuxer.StreamInfo>();
            foreach (var item in response.getList("streams"))
            {
                if (item is not HTSMessage stream || !stream.containsField("index") || !stream.containsField("type"))
                {
                    continue;
                }

                streams.Add(new HtspTransportStreamMuxer.StreamInfo
                {
                    Index = stream.getInt("index"),
                    Codec = stream.getString("type")
                });
            }

            _muxer.SetStreams(streams);
            _logger.LogInformation("HTSP stream metadata received: {Count} playable stream(s)", streams.Count);
        }

        public void Dispose()
        {
            Close().GetAwaiter().GetResult();
            _stream.Dispose();
        }

        internal static bool LooksLikeTransportStream(byte[] payload)
        {
            if (payload == null || payload.Length < 188 || payload.Length % 188 != 0)
            {
                return false;
            }

            var packetsToCheck = Math.Min(payload.Length / 188, 8);
            for (var i = 0; i < packetsToCheck; i++)
            {
                if (payload[i * 188] != 0x47)
                {
                    return false;
                }
            }

            return true;
        }

        private sealed class TaskResponseHandler : HTSResponseHandler
        {
            private readonly TaskCompletionSource<HTSMessage> _task = new TaskCompletionSource<HTSMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<HTSMessage> Task => _task.Task;

            public void handleResponse(HTSMessage response)
            {
                _task.TrySetResult(response);
            }
        }

        private sealed class BlockingByteStream : Stream
        {
            private readonly Queue<byte[]> _chunks = new Queue<byte[]>();
            private byte[] _current;
            private int _currentOffset;
            private bool _completed;
            private Exception _error;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public void WriteChunk(byte[] chunk)
            {
                if (chunk == null || chunk.Length == 0)
                {
                    return;
                }

                lock (_chunks)
                {
                    if (_completed)
                    {
                        return;
                    }

                    _chunks.Enqueue(chunk);
                    Monitor.PulseAll(_chunks);
                }
            }

            public void Complete(Exception error = null)
            {
                lock (_chunks)
                {
                    _error = error;
                    _completed = true;
                    Monitor.PulseAll(_chunks);
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                lock (_chunks)
                {
                    while (_current == null || _currentOffset >= _current.Length)
                    {
                        if (_chunks.Count > 0)
                        {
                            _current = _chunks.Dequeue();
                            _currentOffset = 0;
                            break;
                        }

                        if (_error != null)
                        {
                            throw new IOException(_error.Message, _error);
                        }

                        if (_completed)
                        {
                            return 0;
                        }

                        Monitor.Wait(_chunks, TimeSpan.FromSeconds(30));
                    }

                    var bytesToCopy = Math.Min(count, _current.Length - _currentOffset);
                    Array.Copy(_current, _currentOffset, buffer, offset, bytesToCopy);
                    _currentOffset += bytesToCopy;
                    return bytesToCopy;
                }
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }
    }
}
