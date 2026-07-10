using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using TVHeadEnd.Helper;

namespace TVHeadEnd.HTSP
{
    public class HTSMessage
    {
        // Protocol 44 adds absolute signal strength (dBm) and SNR (dB) fields.
        public const long HTSP_CLIENT_VERSION = 44;
        public const long HTSP_MIN_SERVER_VERSION = 19;

        // Backwards-compatible alias used by existing call sites and UI strings.
        public const long HTSP_VERSION = HTSP_CLIENT_VERSION;

        private const byte HMF_MAP = 1;
        private const byte HMF_S64 = 2;
        private const byte HMF_STR = 3;
        private const byte HMF_BIN = 4;
        private const byte HMF_LIST = 5;
        private const byte HMF_DBL = 6;
        private const byte HMF_BOOL = 7;
        private const byte HMF_UUID = 8;

        private readonly Dictionary<string, object> _dict;
        private ILogger<HTSMessage>? _logger;
        private byte[]? _data;

        public HTSMessage()
        {
            _dict = new Dictionary<string, object>();
        }

        public string Method
        {
            get
            {
                return GetString("method", string.Empty) ?? string.Empty;
            }

            set
            {
                _dict["method"] = value;
                _data = null;
            }
        }

        public void PutField(string name, object value)
        {
            if (value != null)
            {
                _dict[name] = value;
                _data = null;
            }
        }

        public void RemoveField(string name)
        {
            _dict.Remove(name);
            _data = null;
        }

        public Dictionary<string, object>.Enumerator GetEnumerator()
        {
            return _dict.GetEnumerator();
        }

        public bool ContainsField(string name)
        {
            return _dict.ContainsKey(name);
        }

        public object GetField(string name)
        {
            return _dict[name];
        }

        public System.Numerics.BigInteger getBigInteger(string name)
        {
            try
            {
                return (System.Numerics.BigInteger)_dict[name];
            }
            catch (InvalidCastException)
            {
                _logger?.LogCritical(
                    "[TVHclient] Caught InvalidCastException for field name '{Name}'. Expected 'System.Numerics.BigInteger' but got '{Type}'",
                    name,
                    _dict[name].GetType());
                throw;
            }
        }

        public long GetLong(string name)
        {
            return (long)GetBigInteger(name);
        }

        public long GetLong(string name, long std)
        {
            if (!ContainsField(name))
            {
                return std;
            }

            return GetLong(name);
        }

        public int GetInt(string name)
        {
            return (int)GetBigInteger(name);
        }

        public int GetInt(string name, int std)
        {
            if (!ContainsField(name))
            {
                return std;
            }

            return GetInt(name);
        }

        public bool getBool(string name)
        {
            object obj = _dict[name];
            if (obj is bool boolValue)
            {
                return boolValue;
            }

            if (obj is System.Numerics.BigInteger intValue)
            {
                return intValue != 0;
            }

            return Convert.ToBoolean(obj);
        }

        public bool getBool(string name, bool std)
        {
            if (!containsField(name))
            {
                return std;
            }
            return getBool(name);
        }

        public double getDouble(string name)
        {
            object obj = _dict[name];
            if (obj is double doubleValue)
            {
                return doubleValue;
            }

            if (obj is float floatValue)
            {
                return floatValue;
            }

            if (obj is System.Numerics.BigInteger intValue)
            {
                return (double)intValue;
            }

            return Convert.ToDouble(obj);
        }

        public double getDouble(string name, double std)
        {
            if (!containsField(name))
            {
                return std;
            }
            return getDouble(name);
        }

        public string getString(string name, string std)
        {
            if (!ContainsField(name))
            {
                return std;
            }

            return GetString(name);
        }

        public string? GetString(string name)
        {
            object obj = _dict[name];
            if (obj == null)
            {
                return null;
            }

            return obj.ToString();
        }

        public IList GetList(string name)
        {
            return (IList)_dict[name];
        }

        public byte[] GetByteArray(string name)
        {
            return (byte[])_dict[name];
        }

        public byte[] BuildBytes()
        {
            if (_data != null)
            {
                return _data;
            }

            byte[] buf = Array.Empty<byte>();

            // calc data
            byte[] data = SerializeBinary(_dict);

            // calc length
            int len = data.Length;
            byte[] tmpByte = new byte[1];
            tmpByte[0] = unchecked((byte)((len >> 24) & 0xFF));
            buf = buf.Concat(tmpByte).ToArray();
            tmpByte[0] = unchecked((byte)((len >> 16) & 0xFF));
            buf = buf.Concat(tmpByte).ToArray();
            tmpByte[0] = unchecked((byte)((len >> 8) & 0xFF));
            buf = buf.Concat(tmpByte).ToArray();
            tmpByte[0] = unchecked((byte)(len & 0xFF));
            buf = buf.Concat(tmpByte).ToArray();

            // append data
            buf = buf.Concat(data).ToArray();

            return buf;
        }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("\nHTSMessage:\n");
            sb.Append("  <dump>\n");
            sb.Append(GetValueString(_dict, "    "));
            sb.Append("  </dump>\n\n");
            return sb.ToString();
        }

        private string GetValueString(object? value, string pad)
        {
            if (value is byte[])
            {
                StringBuilder sb = new StringBuilder();
                byte[] bVal = (byte[])value;
                for (int ii = 0; ii < bVal.Length; ii++)
                {
                    sb.Append(bVal[ii]);
                    // sb.Append(" (" + Convert.ToString(bVal[ii], 2).PadLeft(8, '0') + ")");
                    sb.Append(", ");
                }

                return sb.ToString();
            }
            else if (value is IDictionary)
            {
                StringBuilder sb = new StringBuilder();
                IDictionary dictVal = (IDictionary)value;
                foreach (object key in dictVal.Keys)
                {
                    object? currValue = dictVal[key];
                    sb.Append(pad + key + " : " + GetValueString(currValue, pad + "  ") + "\n");
                }

                return sb.ToString();
            }
            else if (value is ICollection)
            {
                StringBuilder sb = new StringBuilder();
                ICollection colVal = (ICollection)value;
                foreach (object tmpObj in colVal)
                {
                    sb.Append(GetValueString(tmpObj, pad) + ", ");
                }

                return sb.ToString();
            }

            return string.Empty + value;
        }

        private byte[] SerializeBinary(IDictionary map)
        {
            byte[] buf = Array.Empty<byte>();
            foreach (object key in map.Keys)
            {
                object? value = map[key];
                byte[] sub = SerializeBinary(key.ToString() ?? string.Empty, value);
                buf = buf.Concat(sub).ToArray();
            }

            return buf;
        }

        private byte[] SerializeBinary(ICollection list)
        {
            byte[] buf = Array.Empty<byte>();
            foreach (object value in list)
            {
                byte[] sub = SerializeBinary(string.Empty, value);
                buf = buf.Concat(sub).ToArray();
            }

            return buf;
        }

        private byte[] SerializeBinary(string name, object? value)
        {
            byte[] bName = GetBytes(name);
            byte[] bData = Array.Empty<byte>();
            byte type;

            if (value is string)
            {
                type = HTSMessage.HmfStr;
                bData = GetBytes((string)value);
            }
            else if (value is System.Numerics.BigInteger)
            {
                type = HTSMessage.HmfS64;
                bData = ToByteArray((System.Numerics.BigInteger)value);
            }
            else if (value is int?)
            {
                type = HTSMessage.HmfS64;
                bData = ToByteArray((int)value);
            }
            else if (value is long?)
            {
                type = HTSMessage.HmfS64;
                bData = ToByteArray((long)value);
            }
            else if (value is bool)
            {
                type = HTSMessage.HMF_BOOL;
                bData = (bool)value ? new byte[] { 1 } : new byte[0];
            }
            else if (value is double)
            {
                type = HTSMessage.HMF_DBL;
                bData = BitConverter.GetBytes((double)value);
            }
            else if (value is float)
            {
                type = HTSMessage.HMF_DBL;
                bData = BitConverter.GetBytes((double)(float)value);
            }
            else if (value is byte[])
            {
                type = HTSMessage.HmfBin;
                bData = (byte[])value;
            }
            else if (value is IDictionary)
            {
                type = HTSMessage.HmfMap;
                bData = SerializeBinary((IDictionary)value);
            }
            else if (value is ICollection)
            {
                type = HTSMessage.HmfList;
                bData = SerializeBinary((ICollection)value);
            }
            else if (value == null)
            {
                throw new IOException("[TVHclient] HTSPMessage.getValueString: HTSP doesn't support null values");
            }
            else
            {
                throw new IOException("[TVHclient] HTSPMessage.getValueString: unhandled class for " + name + ": " + value + " (" + value.GetType().Name + ")");
            }

            byte[] buf = new byte[1 + 1 + 4 + bName.Length + bData.Length];
            buf[0] = type;
            buf[1] = unchecked((byte)(bName.Length & 0xFF));
            buf[2] = unchecked((byte)((bData.Length >> 24) & 0xFF));
            buf[3] = unchecked((byte)((bData.Length >> 16) & 0xFF));
            buf[4] = unchecked((byte)((bData.Length >> 8) & 0xFF));
            buf[5] = unchecked((byte)(bData.Length & 0xFF));

            Array.Copy(bName, 0, buf, 6, bName.Length);
            Array.Copy(bData, 0, buf, 6 + bName.Length, bData.Length);

            return buf;
        }

        private byte[] ToByteArray(System.Numerics.BigInteger big)
        {
            if (big < long.MinValue || big > long.MaxValue)
            {
                throw new IOException("[TVHclient] HTSPMessage.toByteArray: S64 value is outside the signed 64-bit range: " + big);
            }

            long value = (long)big;
            byte[] bytes = BitConverter.GetBytes(value);

            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            // HTSP S64 values are encoded little-endian with only redundant
            // most-significant zero bytes removed. Keep zero bytes that occur
            // inside the value; dropping them changes values such as 65536.
            // Negative S64 values must be sent as the full 8-byte two's
            // complement representation.
            if (value < 0)
            {
                return bytes;
            }

            int length = bytes.Length;
            while (length > 1 && bytes[length - 1] == 0)
            {
                length--;
            }

            byte[] result = new byte[length];
            Array.Copy(bytes, result, length);
            return result;
        }

        public static HTSMessage? Parse(byte[] data, ILogger<HTSMessage> logger)
        {
            if (data.Length < 4)
            {
                logger.LogError("[TVHclient] HTSMessage.parse(byte[]): didn't receive enough data");
                return null;
            }

            long len = UIntToLong(data[0], data[1], data[2], data[3]);
            // Message not fully read
            if (data.Length < len + 4)
            {
                logger.LogError("[TVHclient] HTSMessage.parse(byte[]): didn't receive enough data for len: {Len}", len);
                return null;
            }

            // drops 4 bytes (length information)
            byte[] messageData = new byte[len];
            Array.Copy(data, 4, messageData, 0, len);

            HTSMessage msg = DeserializeBinary(messageData);

            msg._logger = logger;
            msg._data = data;

            return msg;
        }

        public static long UIntToLong(byte b1, byte b2, byte b3, byte b4)
        {
            long i = 0;
            i <<= 8;
            i ^= b1 & 0xFF;
            i <<= 8;
            i ^= b2 & 0xFF;
            i <<= 8;
            i ^= b3 & 0xFF;
            i <<= 8;
            i ^= b4 & 0xFF;
            return i;
        }

        private static System.Numerics.BigInteger ToBigInteger(byte[] b)
        {
            byte[] b1 = new byte[8];
            for (int ii = 0; ii < b.Length; ii++)
            {
                b1[ii] = b[ii];
            }

            long lValue = BitConverter.ToInt64(b1, 0);
            return new System.Numerics.BigInteger(lValue);
        }

        private static HTSMessage DeserializeBinary(byte[] messageData)
        {
            byte type, namelen;
            long datalen;

            HTSMessage msg = new HTSMessage();
            int cnt = 0;

            ByteBuffer buf = new ByteBuffer(messageData);
            while (buf.HasRemaining())
            {
                type = buf.Get();
                namelen = buf.Get();
                datalen = UIntToLong(buf.Get(), buf.Get(), buf.Get(), buf.Get());

                if (buf.Length() < namelen + datalen)
                {
                    throw new IOException("[TVHclient] HTSMessage.deserializeBinary: buffer limit exceeded");
                }

                // Get the key for the map (the name)
                string name;
                if (namelen == 0)
                {
                    name = Convert.ToString(cnt++, CultureInfo.InvariantCulture);
                }
                else
                {
                    byte[] bName = new byte[namelen];
                    buf.Get(bName);
                    name = NewString(bName);
                }

                // Get the actual content
                object? obj;
                byte[] bData = new byte[datalen];
                buf.Get(bData);

                bool decoded = true;
                switch (type)
                {
                    case HTSMessage.HmfStr:
                        {
                            obj = NewString(bData);
                            break;
                        }

                    case HmfBin:
                        {
                            obj = bData;
                            break;
                        }

                    case HmfS64:
                        {
                            obj = ToBigInteger(bData);
                            break;
                        }

                    case HmfMap:
                        {
                            obj = DeserializeBinary(bData);
                            break;
                        }

                    case HmfList:
                        {
                            obj = new List<object>(DeserializeBinary(bData)._dict.Values);
                            break;
                        }
                    case HMF_DBL:
                        {
                            if (bData.Length == sizeof(double))
                            {
                                obj = BitConverter.ToDouble(bData, 0);
                            }
                            else
                            {
                                decoded = false;
                            }
                            break;
                        }
                    case HMF_BOOL:
                        {
                            obj = bData.Length == 1 && bData[0] != 0;
                            break;
                        }
                    case HMF_UUID:
                        {
                            // Keep UUID payloads as raw bytes. Tvheadend treats UUID as a
                            // distinct HTSMSG type, but this client only needs to avoid
                            // failing when newer servers include it in replies.
                            obj = bData;
                            break;
                        }
                    default:
                        {
                            // Forward compatibility: ignore fields with newer HTSMSG types
                            // instead of dropping the whole message.
                            decoded = false;
                            break;
                        }
                }

                if (decoded)
                {
                    msg.putField(name, obj);
                }
            }

            return msg;
        }

        private static string NewString(byte[] bytes)
        {
            return System.Text.Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }

        private byte[] GetBytes(string s)
        {
            System.Text.Encoding encoding = System.Text.Encoding.UTF8;
            byte[] bytes = new byte[encoding.GetByteCount(s)];
            encoding.GetBytes(s, 0, s.Length, bytes, 0);
            return bytes;
        }
    }
}
