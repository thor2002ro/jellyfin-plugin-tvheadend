using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TVHeadEnd
{
    internal sealed class HtspTransportStreamMuxer
    {
        private const int PacketSize = 188;
        private const int PmtPid = 0x100;

        private readonly Dictionary<int, StreamInfo> _streams = new Dictionary<int, StreamInfo>();
        private readonly Dictionary<int, byte> _continuityCounters = new Dictionary<int, byte>();
        private bool _wroteTables;
        private int _nextPid = 0x101;
        private int _nextVideoStreamId;
        private int _nextAudioStreamId;

        public bool HasStreams => _streams.Count > 0;

        public void SetStreams(IEnumerable<StreamInfo> streams)
        {
            _streams.Clear();
            _continuityCounters.Clear();
            _wroteTables = false;
            _nextPid = 0x101;
            _nextVideoStreamId = 0;
            _nextAudioStreamId = 0;

            foreach (var stream in streams)
            {
                if (!TryGetTsType(stream.Codec, out var streamType, out var isVideo))
                {
                    continue;
                }

                stream.Pid = _nextPid++;
                stream.StreamType = streamType;
                stream.StreamId = isVideo ? 0xE0 + _nextVideoStreamId++ : 0xC0 + _nextAudioStreamId++;
                _streams[stream.Index] = stream;
            }
        }

        public byte[] WritePacket(int streamIndex, byte[] payload, long? pts, long? dts)
        {
            if (!_streams.TryGetValue(streamIndex, out var stream) || payload == null || payload.Length == 0)
            {
                return Array.Empty<byte>();
            }

            using var output = new MemoryStream();
            if (!_wroteTables)
            {
                WriteTables(output);
                _wroteTables = true;
            }

            var pes = BuildPes(stream.StreamId, payload, ToTsTimestamp(pts), ToTsTimestamp(dts));
            WriteTsPackets(output, stream.Pid, true, pes);
            return output.ToArray();
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
            var pcrPid = streams.FirstOrDefault(i => i.StreamId >= 0xE0)?.Pid ?? streams.First().Pid;
            var sectionLength = 9 + streams.Count * 5 + 4;
            var section = new List<byte>
            {
                0x02,
                (byte)(0xB0 | ((sectionLength >> 8) & 0x0F)),
                (byte)(sectionLength & 0xFF),
                0x00,
                0x01,
                0xC1,
                0x00,
                0x00,
                (byte)(0xE0 | ((pcrPid >> 8) & 0x1F)),
                (byte)(pcrPid & 0xFF),
                0xF0,
                0x00
            };

            foreach (var stream in streams)
            {
                section.Add(stream.StreamType);
                section.Add((byte)(0xE0 | ((stream.Pid >> 8) & 0x1F)));
                section.Add((byte)(stream.Pid & 0xFF));
                section.Add(0xF0);
                section.Add(0x00);
            }

            AppendCrc(section);
            return section.ToArray();
        }

        private static byte[] BuildPes(int streamId, byte[] payload, long? pts, long? dts)
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
            output.WriteByte(0x80);
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
            WriteTsPackets(output, pid, true, data);
        }

        private void WriteTsPackets(Stream output, int pid, bool payloadUnitStart, byte[] data)
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
                var payloadCapacity = 184;
                var useAdaptation = remaining < payloadCapacity;
                if (useAdaptation)
                {
                    payloadCapacity = remaining;
                    var adaptationLength = 183 - payloadCapacity;
                    packet[3] = (byte)(0x30 | NextCounter(pid));
                    packet[4] = (byte)adaptationLength;
                    if (adaptationLength > 0)
                    {
                        packet[5] = 0x00;
                        for (var i = 6; i < 5 + adaptationLength; i++)
                        {
                            packet[i] = 0xFF;
                        }
                    }

                    Array.Copy(data, offset, packet, 5 + adaptationLength, payloadCapacity);
                }
                else
                {
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

        private static long? ToTsTimestamp(long? htspTimestamp)
        {
            return htspTimestamp.HasValue ? htspTimestamp.Value * 90 / 1000 : null;
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

        private static bool TryGetTsType(string codec, out byte streamType, out bool isVideo)
        {
            isVideo = false;
            switch ((codec ?? string.Empty).ToUpperInvariant())
            {
                case "MPEGTS":
                case "MPEG2VIDEO":
                    streamType = 0x02;
                    isVideo = true;
                    return true;
                case "H264":
                    streamType = 0x1B;
                    isVideo = true;
                    return true;
                case "HEVC":
                case "H265":
                    streamType = 0x24;
                    isVideo = true;
                    return true;
                case "AAC":
                    streamType = 0x0F;
                    return true;
                case "MPEG2AUDIO":
                case "MP2":
                    streamType = 0x03;
                    return true;
                case "AC3":
                    streamType = 0x81;
                    return true;
                case "EAC3":
                    streamType = 0x87;
                    return true;
                default:
                    streamType = 0;
                    return false;
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

        internal sealed class StreamInfo
        {
            public int Index { get; set; }

            public string Codec { get; set; }

            public int Pid { get; set; }

            public byte StreamType { get; set; }

            public int StreamId { get; set; }
        }
    }
}
