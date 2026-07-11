using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace TVHeadEnd
{
    internal sealed class HtspTransportStreamMuxer
    {
        private const int PacketSize = 188;
        private const int PmtPid = 0x100;
        private const int FirstElementaryPid = 0x101;
        private const long MaxAudioTimestampRegression90Khz = 9000; // 100 ms
        private const long TimestampBackwardDiscontinuity90Khz = 90000; // 1 second
        private const long TimestampForwardDiscontinuity90Khz = 900000; // 10 seconds
        private const long TimestampWrap90Khz = 1L << 33;
        private static readonly TimeSpan TableRepeatInterval = TimeSpan.FromMilliseconds(100);

        private readonly Dictionary<int, StreamInfo> _streams = new Dictionary<int, StreamInfo>();
        private readonly Dictionary<int, byte> _continuityCounters = new Dictionary<int, byte>();
        private readonly HashSet<int> _pendingDiscontinuityPids = new HashSet<int>();
        private bool _wroteTables;
        private bool _timestampsAre90Khz;
        private int _nextPid = FirstElementaryPid;
        private int _nextVideoStreamId;
        private int _nextAudioStreamId;
        private long _lastTablesTimestamp;
        private int _pcrPid = PmtPid;
        private long? _timestampBase;
        private long? _lastUnwrappedTimestamp90Khz;
        private long? _lastMuxClock;
        private string _streamLayoutSignature;
        private byte _pmtVersion;

        public bool HasStreams => _streams.Count > 0;

        public int SupportedStreamCount => _streams.Count;

        public int PmtVersion => _pmtVersion;

        public string StreamSummary => string.Join(", ", _streams.Values
            .OrderBy(i => i.Pid)
            .Select(i => $"{i.Index}:{(i.Codec ?? "UNKNOWN")}->{i.StreamTypeDescription}@0x{i.Pid:X}/type0x{i.StreamType:X2}"));

        public void SetTimestampsAre90Khz(bool timestampsAre90Khz)
        {
            _timestampsAre90Khz = timestampsAre90Khz;
        }

        public bool SetStreams(IEnumerable<StreamInfo> streams, bool sourceDiscontinuity = false)
        {
            var previousStreams = _streams.ToDictionary(i => i.Key, i => i.Value);
            var hadPreviousLayout = _streamLayoutSignature != null;
            var preserveTimeline = hadPreviousLayout && !sourceDiscontinuity;

            _streams.Clear();
            if (!preserveTimeline)
            {
                _continuityCounters.Clear();
                _timestampBase = null;
                _lastUnwrappedTimestamp90Khz = null;
                _lastMuxClock = null;
            }

            // Preserve any one-shot discontinuity flags that have not yet been
            // emitted. A duplicate subscriptionStart can arrive before the first
            // packet; clearing here would silently lose the required signal.
            _wroteTables = false;
            _nextPid = FirstElementaryPid;
            _nextVideoStreamId = 0;
            _nextAudioStreamId = 0;
            _pcrPid = PmtPid;

            foreach (var stream in (streams ?? Enumerable.Empty<StreamInfo>())
                .Where(i => i != null)
                .OrderBy(i => i.Index))
            {
                if (preserveTimeline
                    && previousStreams.TryGetValue(stream.Index, out var previous)
                    && string.Equals(NormalizeCodec(previous.Codec), NormalizeCodec(stream.Codec), StringComparison.Ordinal))
                {
                    stream.LastPts90Khz = previous.LastPts90Khz;
                    stream.LastDts90Khz = previous.LastDts90Khz;
                    stream.LastSourceClock90Khz = previous.LastSourceClock90Khz;
                    stream.PendingSourceClock90Khz = previous.PendingSourceClock90Khz;
                    stream.TimestampCorrectionCount = previous.TimestampCorrectionCount;
                    stream.TimestampDiscontinuityCount = previous.TimestampDiscontinuityCount;
                }
                else
                {
                    stream.LastPts90Khz = null;
                    stream.LastDts90Khz = null;
                    stream.LastSourceClock90Khz = null;
                    stream.PendingSourceClock90Khz = null;
                    stream.TimestampCorrectionCount = 0;
                    stream.TimestampDiscontinuityCount = 0;
                }

                if (!TryConfigureStream(stream))
                {
                    continue;
                }

                stream.Pid = _nextPid++;
                _streams[stream.Index] = stream;
            }

            var pcrStream = _streams.Values.FirstOrDefault(i => i.Kind == ElementaryStreamKind.Video)
                ?? _streams.Values.FirstOrDefault();
            if (pcrStream != null)
            {
                _pcrPid = pcrStream.Pid;
            }

            var newSignature = BuildStreamLayoutSignature();
            var layoutChanged = hadPreviousLayout
                && !string.Equals(_streamLayoutSignature, newSignature, StringComparison.Ordinal);
            if (layoutChanged)
            {
                _pmtVersion = (byte)((_pmtVersion + 1) & 0x1F);
            }

            _streamLayoutSignature = newSignature;

            var validPids = new HashSet<int>(_streams.Values.Select(i => i.Pid)) { 0, PmtPid };
            foreach (var pid in _continuityCounters.Keys.Where(i => !validPids.Contains(i)).ToList())
            {
                _continuityCounters.Remove(pid);
            }

            _pendingDiscontinuityPids.RemoveWhere(pid => !validPids.Contains(pid));
            if (sourceDiscontinuity || layoutChanged)
            {
                _pendingDiscontinuityPids.Clear();
                MarkDiscontinuityForAllPids();
            }

            return layoutChanged;
        }

        private string BuildStreamLayoutSignature()
        {
            return string.Join(
                "|",
                _streams.Values
                    .OrderBy(i => i.Pid)
                    .Select(i => string.Join(
                        ":",
                        i.Index,
                        i.Pid,
                        i.StreamType,
                        NormalizeCodec(i.Codec),
                        i.Language ?? string.Empty,
                        i.Channels,
                        i.Rate,
                        i.CompositionId,
                        i.AncillaryId,
                        Convert.ToHexString(i.Descriptors ?? Array.Empty<byte>()))));
        }

        private void MarkDiscontinuityForAllPids()
        {
            _pendingDiscontinuityPids.Add(0);
            _pendingDiscontinuityPids.Add(PmtPid);
            foreach (var pid in _streams.Values.Select(i => i.Pid))
            {
                _pendingDiscontinuityPids.Add(pid);
            }
        }

        public void MarkSourceDiscontinuity()
        {
            _wroteTables = false;
            _continuityCounters.Clear();
            ResetTimestampState();
            MarkDiscontinuityForAllPids();
        }

        public void MarkStreamDiscontinuity(int streamIndex, bool repeatProgramTables)
        {
            if (!_streams.TryGetValue(streamIndex, out var stream))
            {
                return;
            }

            _pendingDiscontinuityPids.Add(stream.Pid);
            if (repeatProgramTables)
            {
                _wroteTables = false;
                _pendingDiscontinuityPids.Add(0);
                _pendingDiscontinuityPids.Add(PmtPid);
            }
        }

        public bool IsStreamKnown(int streamIndex)
        {
            return _streams.ContainsKey(streamIndex);
        }

        public StreamInfo GetStreamInfo(int streamIndex)
        {
            return _streams.TryGetValue(streamIndex, out var stream) ? stream : null;
        }

        internal static bool CanMuxCodec(string codec)
        {
            return TryGetTsType(NormalizeCodec(codec), out _, out _, out _);
        }

        public byte[] WritePacket(
            int streamIndex,
            byte[] payload,
            long? pts,
            long? dts,
            out bool sourceDiscontinuity,
            bool forceProgramTables = false,
            bool randomAccess = false)
        {
            sourceDiscontinuity = false;
            if (!_streams.TryGetValue(streamIndex, out var stream) || payload == null || payload.Length == 0)
            {
                return Array.Empty<byte>();
            }

            if (forceProgramTables)
            {
                _wroteTables = false;
            }

            var sourceClock = To90KhzTimestamp(dts ?? pts);
            var timestampDecision = EvaluateTimestamp(stream, sourceClock);
            if (timestampDecision == TimestampDecision.Drop)
            {
                return Array.Empty<byte>();
            }

            if (timestampDecision == TimestampDecision.Reset)
            {
                sourceDiscontinuity = true;
                stream.TimestampDiscontinuityCount++;
                MarkSourceDiscontinuity();
                stream.LastSourceClock90Khz = sourceClock;
            }

            var tsDts = ToTsTimestamp(dts);
            var tsPts = ToTsTimestamp(pts);

            using var output = new MemoryStream();
            var now = Stopwatch.GetTimestamp();
            if (!_wroteTables || Stopwatch.GetElapsedTime(_lastTablesTimestamp, now) >= TableRepeatInterval)
            {
                WriteTables(output);
                _wroteTables = true;
                _lastTablesTimestamp = now;
            }

            if (IsDvbSubtitleCodec(stream.Codec) && !tsPts.HasValue && _lastMuxClock.HasValue)
            {
                // Android/ExoPlayer is stricter than FFmpeg for live TS subtitles:
                // DVB subtitle PES packets without PTS can be demuxed but not scheduled
                // for rendering. If TVHeadend omits a subtitle PTS, schedule it at
                // the latest known mux clock rather than emitting an undated PES.
                tsPts = _lastMuxClock.Value;
            }

            NormalizeAudioTimestamps(stream, ref tsPts, ref tsDts);

            var effectiveClock = tsDts ?? tsPts;
            if (effectiveClock.HasValue)
            {
                _lastMuxClock = effectiveClock.Value;
            }

            var pesPayload = PreparePayloadForPes(stream, payload);
            var dataAligned = stream.Kind == ElementaryStreamKind.Video || IsDvbSubtitleCodec(stream.Codec);
            var pes = BuildPes(stream.StreamId, pesPayload, tsPts, tsDts, dataAligned);
            var pcr = stream.Pid == _pcrPid ? effectiveClock : null;
            WriteTsPackets(output, stream.Pid, true, pes, pcr, randomAccess);
            return output.ToArray();
        }

        private TimestampDecision EvaluateTimestamp(StreamInfo stream, long? clock)
        {
            if (!clock.HasValue || stream == null || stream.Kind == ElementaryStreamKind.Private)
            {
                return TimestampDecision.Accept;
            }

            var previous = stream.LastSourceClock90Khz;
            if (!previous.HasValue)
            {
                stream.LastSourceClock90Khz = clock;
                stream.PendingSourceClock90Khz = null;
                return TimestampDecision.Accept;
            }

            if (IsExpectedTimestampDelta(GetTimestampDelta(previous.Value, clock.Value)))
            {
                stream.LastSourceClock90Khz = clock;
                stream.PendingSourceClock90Khz = null;
                return TimestampDecision.Accept;
            }

            if (stream.PendingSourceClock90Khz.HasValue
                && IsExpectedTimestampDelta(GetTimestampDelta(stream.PendingSourceClock90Khz.Value, clock.Value)))
            {
                stream.PendingSourceClock90Khz = null;
                return TimestampDecision.Reset;
            }

            stream.PendingSourceClock90Khz = clock;
            return TimestampDecision.Drop;
        }

        private static bool IsExpectedTimestampDelta(long delta)
        {
            return delta >= -TimestampBackwardDiscontinuity90Khz && delta <= TimestampForwardDiscontinuity90Khz;
        }

        private static long GetTimestampDelta(long previous, long current)
        {
            var delta = current - previous;
            if (current >= 0 && current < TimestampWrap90Khz
                && previous >= 0 && previous < TimestampWrap90Khz)
            {
                if (delta < -(TimestampWrap90Khz / 2))
                {
                    delta += TimestampWrap90Khz;
                }
                else if (delta > TimestampWrap90Khz / 2)
                {
                    delta -= TimestampWrap90Khz;
                }
            }

            return delta;
        }

        private void ResetTimestampState()
        {
            _timestampBase = null;
            _lastUnwrappedTimestamp90Khz = null;
            _lastMuxClock = null;
            foreach (var stream in _streams.Values)
            {
                stream.LastPts90Khz = null;
                stream.LastDts90Khz = null;
                stream.LastSourceClock90Khz = null;
                stream.PendingSourceClock90Khz = null;
            }
        }

        private static void NormalizeAudioTimestamps(StreamInfo stream, ref long? pts, ref long? dts)
        {
            if (stream == null || stream.Kind != ElementaryStreamKind.Audio)
            {
                return;
            }

            var corrected = false;
            pts = ClampSmallTimestampRegression(pts, ref stream.LastPts90Khz, ref corrected);
            dts = ClampSmallTimestampRegression(dts, ref stream.LastDts90Khz, ref corrected);

            if (corrected)
            {
                stream.TimestampCorrectionCount++;
            }
        }

        private static long? ClampSmallTimestampRegression(long? timestamp, ref long? previousTimestamp, ref bool corrected)
        {
            if (!timestamp.HasValue)
            {
                return null;
            }

            var value = timestamp.Value;
            if (previousTimestamp.HasValue && value <= previousTimestamp.Value)
            {
                var regression = previousTimestamp.Value - value;
                if (regression <= MaxAudioTimestampRegression90Khz)
                {
                    // FFmpeg performs the same minimal repair at the output muxer.
                    // Do it at the HTSP->TS boundary so the decoded/transcoded audio
                    // timeline is already monotonic and avoids libfdk_aac warnings.
                    value = previousTimestamp.Value + 1;
                    corrected = true;
                }
            }

            previousTimestamp = value;
            return value;
        }

        private bool TryConfigureStream(StreamInfo stream)
        {
            var normalizedCodec = NormalizeCodec(stream.Codec);
            if (!TryGetTsType(normalizedCodec, out var streamType, out var kind, out var privateStreamId))
            {
                if (!stream.MuxAsPrivateData)
                {
                    return false;
                }

                stream.IsFallbackPrivateData = true;
                stream.Kind = ElementaryStreamKind.Private;
                stream.StreamType = 0x06;
                stream.StreamId = 0xBD;
                stream.StreamTypeDescription = "PRIVATE(" + (string.IsNullOrWhiteSpace(stream.Codec) ? "UNKNOWN" : stream.Codec.Trim()) + ")";
                stream.Descriptors = BuildDescriptors(stream, normalizedCodec);
                return true;
            }

            stream.IsFallbackPrivateData = false;
            stream.Kind = kind;
            stream.StreamType = streamType;
            stream.Descriptors = BuildDescriptors(stream, normalizedCodec);
            stream.StreamTypeDescription = stream.Codec;
            stream.StreamId = kind switch
            {
                ElementaryStreamKind.Video => 0xE0 + Math.Min(_nextVideoStreamId++, 0x0F),
                ElementaryStreamKind.Audio when !privateStreamId.HasValue => 0xC0 + Math.Min(_nextAudioStreamId++, 0x1F),
                _ => privateStreamId ?? 0xBD
            };
            return true;
        }

        private void WriteTables(Stream output)
        {
            WritePsi(output, 0, BuildPat());
            WritePsi(output, PmtPid, BuildPmt());
        }

        private byte[] BuildPat()
        {
            var section = new List<byte>
            {
                0x00,
                0xB0,
                0x0D,
                0x00,
                0x01,
                0xC1,
                0x00,
                0x00,
                0x00,
                0x01,
                (byte)(0xE0 | ((PmtPid >> 8) & 0x1F)),
                (byte)(PmtPid & 0xFF)
            };
            AppendCrc(section);
            return section.ToArray();
        }

        private byte[] BuildPmt()
        {
            var streams = _streams.Values.OrderBy(i => i.Pid).ToList();
            var sectionLength = 9 + streams.Sum(i => 5 + (i.Descriptors?.Length ?? 0)) + 4;
            var section = new List<byte>
            {
                0x02,
                (byte)(0xB0 | ((sectionLength >> 8) & 0x0F)),
                (byte)(sectionLength & 0xFF),
                0x00,
                0x01,
                (byte)(0xC1 | ((_pmtVersion & 0x1F) << 1)),
                0x00,
                0x00,
                (byte)(0xE0 | ((_pcrPid >> 8) & 0x1F)),
                (byte)(_pcrPid & 0xFF),
                0xF0,
                0x00
            };

            foreach (var stream in streams)
            {
                var descriptors = stream.Descriptors ?? Array.Empty<byte>();
                section.Add(stream.StreamType);
                section.Add((byte)(0xE0 | ((stream.Pid >> 8) & 0x1F)));
                section.Add((byte)(stream.Pid & 0xFF));
                section.Add((byte)(0xF0 | ((descriptors.Length >> 8) & 0x0F)));
                section.Add((byte)(descriptors.Length & 0xFF));
                section.AddRange(descriptors);
            }

            AppendCrc(section);
            return section.ToArray();
        }

        private static bool IsDvbSubtitleCodec(string codec)
        {
            switch (NormalizeCodec(codec))
            {
                case "DVBSUB":
                case "DVBSUBTITLE":
                case "DVB_SUBTITLE":
                    return true;
                default:
                    return false;
            }
        }

        private static byte[] PreparePayloadForPes(StreamInfo stream, byte[] payload)
        {
            if (stream == null || payload == null || payload.Length == 0)
            {
                return payload;
            }

            switch (NormalizeCodec(stream.Codec))
            {
                case "DVBSUB":
                case "DVBSUBTITLE":
                case "DVB_SUBTITLE":
                    return PrepareDvbSubtitlePayload(payload);
                default:
                    return payload;
            }
        }

        private static byte[] PrepareDvbSubtitlePayload(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
            {
                return payload;
            }

            // HTSP muxpkt carries the actual frame data for a stream. DVB subtitle
            // PES payloads in an MPEG-TS private_stream_1 need the DVB subtitle
            // data_identifier/subtitle_stream_id prefix before raw 0x0F subtitle
            // segments, followed by an end marker.
            if (payload.Length >= 2 && payload[0] == 0x20)
            {
                return EnsureDvbSubtitleEndMarker(payload);
            }

            if (payload[0] != 0x0F)
            {
                return payload;
            }

            var addEndMarker = payload[payload.Length - 1] != 0xFF;
            var result = new byte[payload.Length + 2 + (addEndMarker ? 1 : 0)];
            result[0] = 0x20;
            result[1] = 0x00;
            Array.Copy(payload, 0, result, 2, payload.Length);
            if (addEndMarker)
            {
                result[result.Length - 1] = 0xFF;
            }

            return result;
        }

        private static byte[] EnsureDvbSubtitleEndMarker(byte[] payload)
        {
            if (payload.Length > 0 && payload[payload.Length - 1] == 0xFF)
            {
                return payload;
            }

            var result = new byte[payload.Length + 1];
            Array.Copy(payload, 0, result, 0, payload.Length);
            result[result.Length - 1] = 0xFF;
            return result;
        }

        private static byte[] BuildPes(int streamId, byte[] payload, long? pts, long? dts, bool dataAligned)
        {
            var hasPts = pts.HasValue;
            var hasDts = dts.HasValue && dts.Value != pts.GetValueOrDefault();
            var headerDataLength = hasDts ? 10 : (hasPts ? 5 : 0);
            var optionalHeaderLength = 3 + headerDataLength;
            var pesPacketLength = payload.Length + optionalHeaderLength;
            var useZeroLength = streamId >= 0xE0 || pesPacketLength > 0xFFFF;

            using var output = new MemoryStream();
            output.WriteByte(0x00);
            output.WriteByte(0x00);
            output.WriteByte(0x01);
            output.WriteByte((byte)streamId);
            output.WriteByte(useZeroLength ? (byte)0x00 : (byte)((pesPacketLength >> 8) & 0xFF));
            output.WriteByte(useZeroLength ? (byte)0x00 : (byte)(pesPacketLength & 0xFF));
            output.WriteByte(dataAligned ? (byte)0x84 : (byte)0x80);
            output.WriteByte(hasDts ? (byte)0xC0 : (hasPts ? (byte)0x80 : (byte)0x00));
            output.WriteByte((byte)headerDataLength);

            if (hasDts)
            {
                WriteTimestamp(output, 0x03, pts.Value);
                WriteTimestamp(output, 0x01, dts.Value);
            }
            else if (hasPts)
            {
                WriteTimestamp(output, 0x02, pts.Value);
            }

            output.Write(payload, 0, payload.Length);
            return output.ToArray();
        }

        private void WritePsi(Stream output, int pid, byte[] section)
        {
            var data = new byte[section.Length + 1];
            data[0] = 0x00;
            Array.Copy(section, 0, data, 1, section.Length);
            WriteTsPackets(output, pid, true, data, null, false);
        }

        private void WriteTsPackets(Stream output, int pid, bool payloadUnitStart, byte[] data, long? pcr, bool randomAccess)
        {
            var offset = 0;
            var first = true;
            while (offset < data.Length)
            {
                var packet = new byte[PacketSize];
                packet[0] = 0x47;
                packet[1] = (byte)((first && payloadUnitStart ? 0x40 : 0x00) | ((pid >> 8) & 0x1F));
                packet[2] = (byte)(pid & 0xFF);

                var remaining = data.Length - offset;
                var writePcr = first && pcr.HasValue;
                var writeDiscontinuity = first && _pendingDiscontinuityPids.Remove(pid);
                var writeRandomAccess = first && randomAccess;
                var adaptationFlags = (byte)((writeDiscontinuity ? 0x80 : 0x00)
                    | (writeRandomAccess ? 0x40 : 0x00)
                    | (writePcr ? 0x10 : 0x00));
                var minAdaptationLength = adaptationFlags != 0 ? 1 + (writePcr ? 6 : 0) : 0;
                var maxPayloadWithAdaptation = 183 - minAdaptationLength;
                var useAdaptation = adaptationFlags != 0 || remaining < 184;
                int payloadCapacity;

                if (useAdaptation)
                {
                    payloadCapacity = Math.Min(remaining, maxPayloadWithAdaptation);
                    var adaptationLength = 183 - payloadCapacity;
                    packet[3] = (byte)(0x30 | NextCounter(pid));
                    packet[4] = (byte)adaptationLength;

                    if (adaptationLength > 0)
                    {
                        var adaptationOffset = 5;
                        packet[adaptationOffset++] = adaptationFlags;
                        if (writePcr)
                        {
                            WritePcr(packet, adaptationOffset, pcr.Value);
                            adaptationOffset += 6;
                        }

                        while (adaptationOffset < 5 + adaptationLength)
                        {
                            packet[adaptationOffset++] = 0xFF;
                        }
                    }

                    Array.Copy(data, offset, packet, 5 + adaptationLength, payloadCapacity);
                }
                else
                {
                    payloadCapacity = 184;
                    packet[3] = (byte)(0x10 | NextCounter(pid));
                    Array.Copy(data, offset, packet, 4, payloadCapacity);
                }

                output.Write(packet, 0, packet.Length);
                offset += payloadCapacity;
                first = false;
            }
        }

        private byte NextCounter(int pid)
        {
            _continuityCounters.TryGetValue(pid, out var current);
            _continuityCounters[pid] = (byte)((current + 1) & 0x0F);
            return current;
        }

        private long? ToTsTimestamp(long? htspTimestamp)
        {
            var timestamp = To90KhzTimestamp(htspTimestamp);
            if (!timestamp.HasValue)
            {
                return null;
            }

            var unwrapped = UnwrapTimestamp(timestamp.Value);
            if (!_timestampBase.HasValue)
            {
                _timestampBase = unwrapped;
            }

            return Math.Max(0, unwrapped - _timestampBase.Value);
        }

        private long UnwrapTimestamp(long timestamp)
        {
            if (!_lastUnwrappedTimestamp90Khz.HasValue || timestamp < 0 || timestamp >= TimestampWrap90Khz)
            {
                if (!_lastUnwrappedTimestamp90Khz.HasValue || timestamp > _lastUnwrappedTimestamp90Khz.Value)
                {
                    _lastUnwrappedTimestamp90Khz = timestamp;
                }

                return timestamp;
            }

            var previous = _lastUnwrappedTimestamp90Khz.Value;
            var previousWrapped = previous & (TimestampWrap90Khz - 1);
            var delta = timestamp - previousWrapped;
            if (delta < -(TimestampWrap90Khz / 2))
            {
                delta += TimestampWrap90Khz;
            }
            else if (delta > TimestampWrap90Khz / 2)
            {
                delta -= TimestampWrap90Khz;
            }

            var unwrapped = previous + delta;
            if (unwrapped > previous)
            {
                _lastUnwrappedTimestamp90Khz = unwrapped;
            }

            return unwrapped;
        }

        private long? To90KhzTimestamp(long? htspTimestamp)
        {
            return htspTimestamp.HasValue
                ? (_timestampsAre90Khz ? htspTimestamp.Value : htspTimestamp.Value * 90 / 1000)
                : null;
        }

        private static void WriteTimestamp(Stream output, int marker, long timestamp)
        {
            var value = (ulong)timestamp & 0x1FFFFFFFFUL;
            output.WriteByte((byte)((marker << 4) | (int)(((value >> 30) & 0x07) << 1) | 1));
            output.WriteByte((byte)((value >> 22) & 0xFF));
            output.WriteByte((byte)((((value >> 15) & 0x7F) << 1) | 1));
            output.WriteByte((byte)((value >> 7) & 0xFF));
            output.WriteByte((byte)(((value & 0x7F) << 1) | 1));
        }

        private static void WritePcr(byte[] packet, int offset, long timestamp)
        {
            var pcrBase = (ulong)timestamp & 0x1FFFFFFFFUL;
            packet[offset] = (byte)((pcrBase >> 25) & 0xFF);
            packet[offset + 1] = (byte)((pcrBase >> 17) & 0xFF);
            packet[offset + 2] = (byte)((pcrBase >> 9) & 0xFF);
            packet[offset + 3] = (byte)((pcrBase >> 1) & 0xFF);
            packet[offset + 4] = (byte)(((pcrBase & 0x01) << 7) | 0x7E);
            packet[offset + 5] = 0x00;
        }

        private static bool TryGetTsType(string normalizedCodec, out byte streamType, out ElementaryStreamKind kind, out int? privateStreamId)
        {
            kind = ElementaryStreamKind.Private;
            privateStreamId = null;

            switch (normalizedCodec)
            {
                case "MPEG1VIDEO":
                    streamType = 0x01;
                    kind = ElementaryStreamKind.Video;
                    return true;
                case "MPEGTS":
                case "MPEG2VIDEO":
                case "MPEGVIDEO":
                    streamType = 0x02;
                    kind = ElementaryStreamKind.Video;
                    return true;
                case "H264":
                case "AVC":
                    streamType = 0x1B;
                    kind = ElementaryStreamKind.Video;
                    return true;
                case "HEVC":
                case "H265":
                    streamType = 0x24;
                    kind = ElementaryStreamKind.Video;
                    return true;
                case "MPEG4VIDEO":
                case "MPEG4PART2":
                    streamType = 0x10;
                    kind = ElementaryStreamKind.Video;
                    return true;
                case "VC1":
                    streamType = 0xEA;
                    kind = ElementaryStreamKind.Video;
                    privateStreamId = 0xFD;
                    return true;
                case "AAC":
                case "AACADTS":
                case "AACLC":
                case "HEAAC":
                case "MPEG4AUDIO":
                    streamType = 0x0F;
                    kind = ElementaryStreamKind.Audio;
                    return true;
                case "AACLATM":
                case "LATM":
                    streamType = 0x11;
                    kind = ElementaryStreamKind.Audio;
                    return true;
                case "MPEG1AUDIO":
                case "MP3":
                    streamType = 0x03;
                    kind = ElementaryStreamKind.Audio;
                    return true;
                case "MPEG2AUDIO":
                case "MP2":
                case "MPA":
                    streamType = 0x04;
                    kind = ElementaryStreamKind.Audio;
                    return true;
                case "AC3":
                    // DVB-C/Cable TS expects AC-3 as private data (0x06) with
                    // AC-3 descriptors. 0x81 is the ATSC stream type and is
                    // less reliably discovered by Android/ExoPlayer in DVB TS.
                    streamType = 0x06;
                    kind = ElementaryStreamKind.Audio;
                    privateStreamId = 0xBD;
                    return true;
                case "EAC3":
                    // Same as AC-3: use DVB private-data stream type plus E-AC-3
                    // descriptors instead of the ATSC 0x87 stream type.
                    streamType = 0x06;
                    kind = ElementaryStreamKind.Audio;
                    privateStreamId = 0xBD;
                    return true;
                case "DTS":
                case "DCA":
                    streamType = 0x06;
                    kind = ElementaryStreamKind.Audio;
                    privateStreamId = 0xBD;
                    return true;
                case "VORBIS":
                case "OPUS":
                case "TRUEHD":
                    streamType = 0x06;
                    kind = ElementaryStreamKind.Audio;
                    privateStreamId = 0xBD;
                    return true;
                case "DVBSUB":
                case "DVBSUBTITLE":
                case "DVB_SUBTITLE":
                case "DVDSUB":
                case "DVDSUBTITLE":
                case "DVD_SUBTITLE":
                case "PGS":
                case "PGSSUB":
                case "PGSSUBTITLE":
                case "HDMVPGSSUBTITLE":
                case "TELETEXT":
                case "TTXT":
                case "TEXTSUB":
                case "TEXTSUBTITLE":
                case "SRT":
                case "SUBRIP":
                case "SSA":
                case "ASS":
                    streamType = 0x06;
                    kind = ElementaryStreamKind.Private;
                    privateStreamId = 0xBD;
                    return true;
                default:
                    streamType = 0x06;
                    return false;
            }
        }

        private static string NormalizeCodec(string codec)
        {
            return (codec ?? string.Empty)
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .ToUpperInvariant();
        }

        private static byte[] BuildDescriptors(StreamInfo stream, string normalizedCodec)
        {
            var descriptors = new List<byte>();

            if (stream.Kind == ElementaryStreamKind.Audio && HasLanguage(stream.Language))
            {
                var lang = GetIsoLanguageBytes(stream.Language);
                AddDescriptor(descriptors, 0x0A, lang[0], lang[1], lang[2], (byte)(stream.AudioType & 0xFF));
            }

            switch (normalizedCodec)
            {
                case "AC3":
                    AddRegistrationDescriptor(descriptors, "AC-3");
                    AddDescriptor(descriptors, 0x6A, 0x00);
                    break;
                case "EAC3":
                    AddRegistrationDescriptor(descriptors, "EAC3");
                    AddDescriptor(descriptors, 0x7A, 0x00);
                    break;
                case "DTS":
                case "DCA":
                    AddDescriptor(descriptors, 0x7B);
                    break;
                case "VORBIS":
                    AddRegistrationDescriptor(descriptors, "VORB");
                    break;
                case "OPUS":
                    AddRegistrationDescriptor(descriptors, "Opus");
                    break;
                case "TRUEHD":
                    AddRegistrationDescriptor(descriptors, "HDMV");
                    break;
                case "VC1":
                    AddRegistrationDescriptor(descriptors, "VC-1");
                    break;
                case "DVBSUB":
                case "DVBSUBTITLE":
                case "DVB_SUBTITLE":
                    AddDvbSubtitleDescriptor(descriptors, stream);
                    break;
                case "TELETEXT":
                case "TTXT":
                    AddTeletextDescriptor(descriptors, stream);
                    break;
                case "DVDSUB":
                case "DVDSUBTITLE":
                case "DVD_SUBTITLE":
                    AddPrivateDataDescriptor(descriptors, "DVDSUB");
                    break;
                case "PGS":
                case "PGSSUB":
                case "PGSSUBTITLE":
                case "HDMVPGSSUBTITLE":
                    AddPrivateDataDescriptor(descriptors, "PGSSUB");
                    break;
                case "TEXTSUB":
                case "TEXTSUBTITLE":
                case "SRT":
                case "SUBRIP":
                case "SSA":
                case "ASS":
                    AddPrivateDataDescriptor(descriptors, normalizedCodec);
                    break;
            }

            if (stream.IsFallbackPrivateData)
            {
                AddRegistrationDescriptor(descriptors, "HTSP");
                AddPrivateDataDescriptor(descriptors, string.IsNullOrWhiteSpace(stream.Codec) ? "UNKNOWN" : stream.Codec);
            }

            return descriptors.ToArray();
        }

        private static void AddDvbSubtitleDescriptor(List<byte> descriptors, StreamInfo stream)
        {
            var lang = GetIsoLanguageBytes(stream.Language);
            var compositionId = stream.CompositionId & 0xFFFF;
            var ancillaryId = stream.AncillaryId & 0xFFFF;
            if (compositionId == 0)
            {
                compositionId = 1;
            }

            if (ancillaryId == 0)
            {
                ancillaryId = compositionId;
            }

            // Some Android TS extractors are stricter about stream metadata than
            // FFmpeg. Keep the DVB subtitling descriptor, but also include the
            // generic ISO-639 language descriptor so the embedded track is easier
            // for the direct-play track selector to expose.
            AddDescriptor(descriptors, 0x0A, lang[0], lang[1], lang[2], 0x00);

            AddDescriptor(
                descriptors,
                0x59,
                lang[0],
                lang[1],
                lang[2],
                0x10,
                (byte)((compositionId >> 8) & 0xFF),
                (byte)(compositionId & 0xFF),
                (byte)((ancillaryId >> 8) & 0xFF),
                (byte)(ancillaryId & 0xFF));
        }

        private static void AddTeletextDescriptor(List<byte> descriptors, StreamInfo stream)
        {
            var lang = GetIsoLanguageBytes(stream.Language);
            AddDescriptor(descriptors, 0x56, lang[0], lang[1], lang[2], 0x20, 0x00);
        }

        private static void AddRegistrationDescriptor(List<byte> descriptors, string fourCc)
        {
            AddDescriptor(descriptors, 0x05, ToFourCcBytes(fourCc));
        }

        private static void AddPrivateDataDescriptor(List<byte> descriptors, string text)
        {
            var bytes = Encoding.ASCII.GetBytes((text ?? string.Empty).Trim());
            if (bytes.Length > 253)
            {
                bytes = bytes.Take(253).ToArray();
            }

            var payload = new byte[bytes.Length + 1];
            payload[0] = 0x01;
            Array.Copy(bytes, 0, payload, 1, bytes.Length);
            AddDescriptor(descriptors, 0x80, payload);
        }

        private static byte[] ToFourCcBytes(string fourCc)
        {
            var result = Encoding.ASCII.GetBytes((fourCc ?? string.Empty).PadRight(4).Substring(0, 4));
            return result;
        }

        private static bool HasLanguage(string language)
        {
            return !string.IsNullOrWhiteSpace(language);
        }

        private static byte[] GetIsoLanguageBytes(string language)
        {
            var normalized = new string((language ?? string.Empty)
                .Where(char.IsLetter)
                .Take(3)
                .Select(char.ToLowerInvariant)
                .ToArray());
            if (normalized.Length != 3)
            {
                normalized = "und";
            }

            return Encoding.ASCII.GetBytes(normalized);
        }

        private static void AddDescriptor(List<byte> descriptors, byte tag, params byte[] data)
        {
            if ((data?.Length ?? 0) > 255)
            {
                throw new InvalidOperationException("MPEG-TS descriptor payload is too large.");
            }

            descriptors.Add(tag);
            descriptors.Add((byte)(data?.Length ?? 0));
            if (data != null && data.Length > 0)
            {
                descriptors.AddRange(data);
            }
        }

        private static void AppendCrc(List<byte> data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc ^= (uint)b << 24;
                for (var i = 0; i < 8; i++)
                {
                    crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
                }
            }

            data.Add((byte)((crc >> 24) & 0xFF));
            data.Add((byte)((crc >> 16) & 0xFF));
            data.Add((byte)((crc >> 8) & 0xFF));
            data.Add((byte)(crc & 0xFF));
        }

        internal enum ElementaryStreamKind
        {
            Video,
            Audio,
            Private
        }

        private enum TimestampDecision
        {
            Accept,
            Drop,
            Reset
        }

        internal sealed class StreamInfo
        {
            public int Index { get; set; }

            public string Codec { get; set; }

            public string Language { get; set; }

            public string DisplayName { get; set; }

            public byte[] Meta { get; set; }

            public int Width { get; set; }

            public int Height { get; set; }

            public int Channels { get; set; }

            public int Rate { get; set; }

            public int AudioType { get; set; }

            public int CompositionId { get; set; }

            public int AncillaryId { get; set; }

            public int Pid { get; set; }

            public byte StreamType { get; set; }

            public int StreamId { get; set; }

            internal ElementaryStreamKind Kind { get; set; }

            internal byte[] Descriptors { get; set; }

            public bool MuxAsPrivateData { get; set; }

            internal bool IsFallbackPrivateData { get; set; }

            internal string StreamTypeDescription { get; set; }

            internal long? LastPts90Khz;

            internal long? LastDts90Khz;

            internal long? LastSourceClock90Khz;

            internal long? PendingSourceClock90Khz;

            internal long TimestampCorrectionCount;

            internal long TimestampDiscontinuityCount;
        }
    }
}
