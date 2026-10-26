using System;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using NebulaeModel.Contracts;

namespace NebulaeModel.Networking.Serialization
{
    internal class NetDataWriter : INetDataWriter
    {
        protected byte[] _data = new byte[InitialSize];
        protected int _position;
        private const int InitialSize = 64;

        public byte[] Data
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _data;
        }

        public int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _position;
        }

        public static readonly ThreadLocal<UTF8Encoding> uTF8Encoding = new ThreadLocal<UTF8Encoding>(() => new UTF8Encoding(false, true));
        public const int StringBufferMaxLength = 65535;
        private byte[] _stringBuffer;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ResizeIfNeed(int newSize)
        {
            if (_data.Length < newSize)
            {
                Array.Resize(ref _data, Math.Max(newSize, _data.Length * 2));
            }
        }

        public void Reset()
        {
            _position = 0;
        }

        public byte[] CopyData()
        {
            byte[] resultData = new byte[_position];
            Buffer.BlockCopy(_data, 0, resultData, 0, _position);
            return resultData;
        }

        public void Put(float value)
        {
            ResizeIfNeed(_position + 4);
            FastBitConverter.GetBytes(_data, _position, value);
            _position += 4;
        }

        public void Put(double value)
        {
            ResizeIfNeed(_position + 8);
            FastBitConverter.GetBytes(_data, _position, value);
            _position += 8;
        }

        public void Put(long value)
        {
            ResizeIfNeed(_position + 8);
            FastBitConverter.GetBytes(_data, _position, value);
            _position += 8;
        }

        public void Put(ulong value)
        {
            ResizeIfNeed(_position + 8);
            FastBitConverter.GetBytes(_data, _position, value);
            _position += 8;
        }

        public void Put(int value)
        {
            ResizeIfNeed(_position + 4);
            FastBitConverter.GetBytes(_data, _position, value);
            _position += 4;
        }

        public void Put(uint value)
        {
            ResizeIfNeed(_position + 4);
            FastBitConverter.GetBytes(_data, _position, value);
            _position += 4;
        }

        public void Put(char value)
        {
            Put((ushort)value);
        }

        public void Put(ushort value)
        {
            ResizeIfNeed(_position + 2);
            FastBitConverter.GetBytes(_data, _position, value);
            _position += 2;
        }

        public void Put(short value)
        {
            ResizeIfNeed(_position + 2);
            FastBitConverter.GetBytes(_data, _position, value);
            _position += 2;
        }

        public void Put(sbyte value)
        {
            ResizeIfNeed(_position + 1);
            _data[_position] = (byte)value;
            _position++;
        }

        public void Put(byte value)
        {
            ResizeIfNeed(_position + 1);
            _data[_position] = value;
            _position++;
        }

        public void Put(byte[] data, int offset, int length)
        {
            ResizeIfNeed(_position + length);
            Buffer.BlockCopy(data, offset, _data, _position, length);
            _position += length;
        }

        public void Put(byte[] data)
        {
            ResizeIfNeed(_position + data.Length);
            Buffer.BlockCopy(data, 0, _data, _position, data.Length);
            _position += data.Length;
        }

        public void PutSBytesWithLength(sbyte[] data)
        {
            PutArray(data, 1);
        }

        public void PutBytesWithLength(byte[] data)
        {
            PutArray(data, 1);
        }

        public void Put(bool value)
        {
            Put((byte)(value ? 1 : 0));
        }

        public void PutArray(Array arr, int sz)
        {
            const int intSize = 4;
            int length = arr == null ? 0 : arr.Length;
            sz *= length;
            ResizeIfNeed(_position + sz + intSize);
            FastBitConverter.GetBytes(_data, _position, length);
            if (arr != null)
                Buffer.BlockCopy(arr, 0, _data, _position + intSize, sz);
            _position += sz + intSize;
        }

        public void PutArray(float[] value)
        {
            PutArray(value, 4);
        }

        public void PutArray(double[] value)
        {
            PutArray(value, 8);
        }

        public void PutArray(long[] value)
        {
            PutArray(value, 8);
        }

        public void PutArray(ulong[] value)
        {
            PutArray(value, 8);
        }

        public void PutArray(int[] value)
        {
            PutArray(value, 4);
        }

        public void PutArray(uint[] value)
        {
            PutArray(value, 4);
        }

        public void PutArray(ushort[] value)
        {
            PutArray(value, 2);
        }

        public void PutArray(short[] value)
        {
            PutArray(value, 2);
        }

        public void PutArray(bool[] value)
        {
            PutArray(value, 1);
        }

        public void PutArray(string[] value)
        {
            int strArrayLength = value == null ? 0 : value.Length;
            Put(strArrayLength);
            for (int i = 0; i < strArrayLength; i++)
                Put(value[i]);
        }

        public void PutArray(string[] value, int strMaxLength)
        {
            ushort strArrayLength = value == null ? (ushort)0 : (ushort)value.Length;
            Put(strArrayLength);
            for (int i = 0; i < strArrayLength; i++)
                Put(value[i], strMaxLength);
        }

        public void Put(IPEndPoint endPoint)
        {
            Put(endPoint.Address.ToString());
            Put(endPoint.Port);
        }

        public void Put(string value)
        {
            Put(value, 0);
        }

        /// <summary>
        /// Note that "maxLength" only limits the number of characters in a string, not its size in bytes.
        /// </summary>
        public void Put(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                Put((ushort)0);
                return;
            }

            int length = maxLength > 0 && value.Length > maxLength ? maxLength : value.Length;
            _stringBuffer ??= new byte[StringBufferMaxLength];
            int size = uTF8Encoding.Value.GetBytes(value, 0, length, _stringBuffer, 0);

            if (size == 0 || size >= StringBufferMaxLength)
            {
                Put((ushort)0);
                return;
            }

            Put(checked((ushort)(size + 1)));
            Put(_stringBuffer, 0, size);
        }

        public void Put<T>(T obj) where T : INetSerializable
        {
            obj.Serialize(this);
        }
    }
}
