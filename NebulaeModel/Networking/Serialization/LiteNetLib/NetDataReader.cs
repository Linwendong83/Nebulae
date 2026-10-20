using System;
using System.Net;
using System.Runtime.CompilerServices;
using NebulaeModel.Contracts;

namespace NebulaeModel.Networking.Serialization
{
    internal class NetDataReader : INetDataReader
    {
        protected byte[] _data;
        protected int _position;
        protected int _dataSize;
        private int _offset;

        public byte[] RawData
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _data;
        }
        public int RawDataSize
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _dataSize;
        }
        public int UserDataOffset
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _offset;
        }
        public int UserDataSize
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _dataSize - _offset;
        }
        public bool IsNull
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _data == null;
        }
        public int Position
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _position;
        }
        public bool EndOfData
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _position == _dataSize;
        }
        public int AvailableBytes
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _dataSize - _position;
        }

        public void SkipBytes(int count)
        {
            EnsureAvailable(count);
            _position += count;
        }

        public void SetPosition(int position)
        {
            if (position < _offset || position > _dataSize)
                throw new ParseException("Invalid reader position");
            _position = position;
        }

        private void EnsureAvailable(int count)
        {
            if (count < 0 || count > AvailableBytes)
                throw new ParseException("Truncated packet payload");
        }

        public void SetSource(INetDataWriter dataWriter)
        {
            _data = dataWriter.Data;
            _position = 0;
            _offset = 0;
            _dataSize = dataWriter.Length;
        }

        public void SetSource(byte[] source)
        {
            _data = source;
            _position = 0;
            _offset = 0;
            _dataSize = source.Length;
        }

        public void SetSource(byte[] source, int offset, int maxSize)
        {
            if (source == null || offset < 0 || maxSize < offset || maxSize > source.Length)
                throw new ParseException("Invalid packet bounds");
            _data = source;
            _position = offset;
            _offset = offset;
            _dataSize = maxSize;
        }

        public NetDataReader()
        {

        }

        public NetDataReader(INetDataWriter writer)
        {
            SetSource(writer);
        }

        public NetDataReader(byte[] source)
        {
            SetSource(source);
        }

        public NetDataReader(byte[] source, int offset, int maxSize)
        {
            SetSource(source, offset, maxSize);
        }

        #region GetMethods
        public IPEndPoint GetNetEndPoint()
        {
            string host = GetString(1000);
            int port = GetInt();
            return NetUtils.MakeEndPoint(host, port);
        }

        public byte GetByte()
        {
            EnsureAvailable(1);
            byte res = _data[_position];
            _position++;
            return res;
        }

        public sbyte GetSByte()
        {
            return (sbyte)GetByte();
        }

        public T[] GetArray<T>(int size)
        {
            if (AvailableBytes < 4 || size <= 0)
                throw new ParseException("Invalid array header");
            int length = BitConverter.ToInt32(_data, _position);
            _position += 4;
            if (length < 0 || length > AvailableBytes / size)
                throw new ParseException("Invalid array length");
            T[] result = new T[length];
            length *= size;
            Buffer.BlockCopy(_data, _position, result, 0, length);
            _position += length;
            return result;
        }

        public bool[] GetBoolArray()
        {
            var length = GetInt();
            EnsureAvailable(length);
            var values = new bool[length];
            for (var i = 0; i < length; i++) values[i] = GetBool();
            return values;
        }

        public ushort[] GetUShortArray()
        {
            return GetArray<ushort>(2);
        }

        public short[] GetShortArray()
        {
            return GetArray<short>(2);
        }

        public int[] GetIntArray()
        {
            return GetArray<int>(4);
        }

        public uint[] GetUIntArray()
        {
            return GetArray<uint>(4);
        }

        public float[] GetFloatArray()
        {
            return GetArray<float>(4);
        }

        public double[] GetDoubleArray()
        {
            return GetArray<double>(8);
        }

        public long[] GetLongArray()
        {
            return GetArray<long>(8);
        }

        public ulong[] GetULongArray()
        {
            return GetArray<ulong>(8);
        }

        public string[] GetStringArray()
        {
            ushort length = GetUShort();
            string[] arr = new string[length];
            for (int i = 0; i < length; i++)
            {
                arr[i] = GetString();
            }
            return arr;
        }

        /// <summary>
        /// Note that "maxStringLength" only limits the number of characters in a string, not its size in bytes.
        /// Strings that exceed this parameter are refused.
        /// </summary>
        public string[] GetStringArray(int maxStringLength)
        {
            ushort length = GetUShort();
            string[] arr = new string[length];
            for (int i = 0; i < length; i++)
            {
                arr[i] = GetString(maxStringLength);
            }
            return arr;
        }

        public bool GetBool()
        {
            var value = GetByte();
            if (value > 1) throw new ParseException("Invalid boolean value");
            return value == 1;
        }

        public char GetChar()
        {
            return (char)GetUShort();
        }

        public ushort GetUShort()
        {
            EnsureAvailable(2);
            ushort result = BitConverter.ToUInt16(_data, _position);
            _position += 2;
            return result;
        }

        public short GetShort()
        {
            EnsureAvailable(2);
            short result = BitConverter.ToInt16(_data, _position);
            _position += 2;
            return result;
        }

        public long GetLong()
        {
            EnsureAvailable(8);
            long result = BitConverter.ToInt64(_data, _position);
            _position += 8;
            return result;
        }

        public ulong GetULong()
        {
            EnsureAvailable(8);
            ulong result = BitConverter.ToUInt64(_data, _position);
            _position += 8;
            return result;
        }

        public int GetInt()
        {
            EnsureAvailable(4);
            int result = BitConverter.ToInt32(_data, _position);
            _position += 4;
            return result;
        }

        public uint GetUInt()
        {
            EnsureAvailable(4);
            uint result = BitConverter.ToUInt32(_data, _position);
            _position += 4;
            return result;
        }

        public float GetFloat()
        {
            EnsureAvailable(4);
            float result = BitConverter.ToSingle(_data, _position);
            _position += 4;
            return result;
        }

        public double GetDouble()
        {
            EnsureAvailable(8);
            double result = BitConverter.ToDouble(_data, _position);
            _position += 8;
            return result;
        }

        /// <summary>
        /// Note that "maxLength" only limits the number of characters in a string, not its size in bytes.
        /// </summary>
        public string GetString(int maxLength)
        {
            ushort size = GetUShort();
            if (size == 0)
            {
                return string.Empty;
            }

            int actualSize = size - 1;
            if (actualSize >= NetDataWriter.StringBufferMaxLength)
            {
                throw new ParseException("Invalid string length");
            }

            ArraySegment<byte> data = GetBytesSegment(actualSize);

            if (maxLength > 0 && NetDataWriter.uTF8Encoding.Value.GetCharCount(data.Array, data.Offset, data.Count) > maxLength)
                throw new ParseException("String exceeds its character limit");
            return NetDataWriter.uTF8Encoding.Value.GetString(data.Array, data.Offset, data.Count);
        }

        public string GetString()
        {
            ushort size = GetUShort();
            if (size == 0)
            {
                return string.Empty;
            }

            int actualSize = size - 1;
            if (actualSize >= NetDataWriter.StringBufferMaxLength)
            {
                throw new ParseException("Invalid string length");
            }

            ArraySegment<byte> data = GetBytesSegment(actualSize);

            return NetDataWriter.uTF8Encoding.Value.GetString(data.Array, data.Offset, data.Count);
        }

        public ArraySegment<byte> GetBytesSegment(int count)
        {
            EnsureAvailable(count);
            ArraySegment<byte> segment = new ArraySegment<byte>(_data, _position, count);
            _position += count;
            return segment;
        }

        public ArraySegment<byte> GetRemainingBytesSegment()
        {
            ArraySegment<byte> segment = new ArraySegment<byte>(_data, _position, AvailableBytes);
            _position = _dataSize;
            return segment;
        }

        public T Get<T>() where T : struct, INetSerializable
        {
            var obj = default(T);
            obj.Deserialize(this);
            return obj;
        }

        public T Get<T>(Func<T> constructor) where T : class, INetSerializable
        {
            var obj = constructor();
            obj.Deserialize(this);
            return obj;
        }

        public byte[] GetRemainingBytes()
        {
            byte[] outgoingData = new byte[AvailableBytes];
            Buffer.BlockCopy(_data, _position, outgoingData, 0, AvailableBytes);
            _position = _dataSize;
            return outgoingData;
        }

        public void GetBytes(byte[] destination, int start, int count)
        {
            EnsureAvailable(count);
            Buffer.BlockCopy(_data, _position, destination, start, count);
            _position += count;
        }

        public void GetBytes(byte[] destination, int count)
        {
            EnsureAvailable(count);
            Buffer.BlockCopy(_data, _position, destination, 0, count);
            _position += count;
        }

        public sbyte[] GetSBytesWithLength()
        {
            return GetArray<sbyte>(1);
        }

        public byte[] GetBytesWithLength()
        {
            return GetArray<byte>(1);
        }
        #endregion

        #region PeekMethods

        public byte PeekByte()
        {
            EnsureAvailable(1);
            return _data[_position];
        }

        public sbyte PeekSByte()
        {
            return (sbyte)PeekByte();
        }

        public bool PeekBool()
        {
            var value = PeekByte();
            if (value > 1) throw new ParseException("Invalid boolean value");
            return value == 1;
        }

        public char PeekChar()
        {
            return (char)PeekUShort();
        }

        public ushort PeekUShort()
        {
            EnsureAvailable(2);
            return BitConverter.ToUInt16(_data, _position);
        }

        public short PeekShort()
        {
            EnsureAvailable(2);
            return BitConverter.ToInt16(_data, _position);
        }

        public long PeekLong()
        {
            EnsureAvailable(8);
            return BitConverter.ToInt64(_data, _position);
        }

        public ulong PeekULong()
        {
            EnsureAvailable(8);
            return BitConverter.ToUInt64(_data, _position);
        }

        public int PeekInt()
        {
            EnsureAvailable(4);
            return BitConverter.ToInt32(_data, _position);
        }

        public uint PeekUInt()
        {
            EnsureAvailable(4);
            return BitConverter.ToUInt32(_data, _position);
        }

        public float PeekFloat()
        {
            EnsureAvailable(4);
            return BitConverter.ToSingle(_data, _position);
        }

        public double PeekDouble()
        {
            EnsureAvailable(8);
            return BitConverter.ToDouble(_data, _position);
        }

        /// <summary>
        /// Note that "maxLength" only limits the number of characters in a string, not its size in bytes.
        /// </summary>
        public string PeekString(int maxLength)
        {
            var position = _position;
            try { return GetString(maxLength); }
            finally { _position = position; }
        }

        public string PeekString() => PeekString(0);
        #endregion

        #region TryGetMethods
        public bool TryGetByte(out byte result)
        {
            if (AvailableBytes >= 1)
            {
                result = GetByte();
                return true;
            }
            result = 0;
            return false;
        }

        public bool TryGetSByte(out sbyte result)
        {
            if (AvailableBytes >= 1)
            {
                result = GetSByte();
                return true;
            }
            result = 0;
            return false;
        }

        public bool TryGetBool(out bool result)
        {
            if (AvailableBytes >= 1 && PeekByte() <= 1)
            {
                result = GetBool();
                return true;
            }
            result = false;
            return false;
        }

        public bool TryGetChar(out char result)
        {
            if (!TryGetUShort(out ushort uShortValue))
            {
                result = '\0';
                return false;
            }
            result = (char)uShortValue;
            return true;
        }

        public bool TryGetShort(out short result)
        {
            if (AvailableBytes >= 2)
            {
                result = GetShort();
                return true;
            }
            result = 0;
            return false;
        }

        public bool TryGetUShort(out ushort result)
        {
            if (AvailableBytes >= 2)
            {
                result = GetUShort();
                return true;
            }
            result = 0;
            return false;
        }

        public bool TryGetInt(out int result)
        {
            if (AvailableBytes >= 4)
            {
                result = GetInt();
                return true;
            }
            result = 0;
            return false;
        }

        public bool TryGetUInt(out uint result)
        {
            if (AvailableBytes >= 4)
            {
                result = GetUInt();
                return true;
            }
            result = 0;
            return false;
        }

        public bool TryGetLong(out long result)
        {
            if (AvailableBytes >= 8)
            {
                result = GetLong();
                return true;
            }
            result = 0;
            return false;
        }

        public bool TryGetULong(out ulong result)
        {
            if (AvailableBytes >= 8)
            {
                result = GetULong();
                return true;
            }
            result = 0;
            return false;
        }

        public bool TryGetFloat(out float result)
        {
            if (AvailableBytes >= 4)
            {
                result = GetFloat();
                return true;
            }
            result = 0;
            return false;
        }

        public bool TryGetDouble(out double result)
        {
            if (AvailableBytes >= 8)
            {
                result = GetDouble();
                return true;
            }
            result = 0;
            return false;
        }

        public bool TryGetString(out string result)
        {
            if (AvailableBytes >= 2)
            {
                ushort strSize = PeekUShort();
                if (AvailableBytes >= (strSize == 0 ? 2 : strSize + 1))
                {
                    result = GetString();
                    return true;
                }
            }
            result = null;
            return false;
        }

        public bool TryGetStringArray(out string[] result)
        {
            if (!TryGetUShort(out ushort strArrayLength))
            {
                result = null;
                return false;
            }

            result = new string[strArrayLength];
            for (int i = 0; i < strArrayLength; i++)
            {
                if (!TryGetString(out result[i]))
                {
                    result = null;
                    return false;
                }
            }

            return true;
        }

        public bool TryGetBytesWithLength(out byte[] result)
        {
            if (AvailableBytes >= 4)
            {
                int length = PeekInt();
                if (length >= 0 && length <= AvailableBytes - 4)
                {
                    result = GetBytesWithLength();
                    return true;
                }
            }
            result = null;
            return false;
        }
        #endregion

        public void Clear()
        {
            _position = 0;
            _dataSize = 0;
            _data = null;
        }
    }
}
