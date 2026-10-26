using System;
using System.Collections.Generic;
using NebulaeModel.Contracts;

namespace NebulaeModel.Networking.Serialization
{
    internal class NetPacketProcessor
    {
        private static class PacketIdCache<T>
        {
            internal static readonly ulong Id = WireContract.PacketId(typeof(T));
        }

        protected delegate void SubscribeDelegate(NetDataReader reader, object userData);

        protected NetSerializer _netSerializer;
        protected readonly Dictionary<ulong, SubscribeDelegate> _callbacks = [];

        public NetPacketProcessor()
        {
            _netSerializer = new NetSerializer();
        }

        internal T ReadFieldLayout<T>(byte[] bytes, out int[] ends) where T : class, new() =>
            _netSerializer.ReadFieldLayout<T>(bytes, out ends);

        protected virtual ulong GetPacketId<T>()
        {
            return PacketIdCache<T>.Id;
        }

        protected virtual SubscribeDelegate GetCallbackFromData(NetDataReader reader)
        {
            ulong id = reader.GetULong();
            if (!_callbacks.TryGetValue(id, out var action))
            {
                throw new ParseException($"Unknown packet identifier: {id}");
            }
            return action;
        }

        protected virtual void WritePacketId<T>(NetDataWriter writer)
        {
            writer.Put(GetPacketId<T>());
        }

        /// <summary>
        /// Register nested property type
        /// </summary>
        /// <typeparam name="T">INetSerializable structure</typeparam>
        public void RegisterNestedType<T>() where T : struct, INetSerializable
        {
            _netSerializer.RegisterNestedType<T>();
        }

        /// <summary>
        /// Register nested property type
        /// </summary>
        /// <param name="writeDelegate"></param>
        /// <param name="readDelegate"></param>
        public void RegisterNestedType<T>(Action<NetDataWriter, T> writeDelegate, Func<NetDataReader, T> readDelegate)
        {
            _netSerializer.RegisterNestedType<T>(writeDelegate, readDelegate);
        }

        /// <summary>
        /// Register nested property type
        /// </summary>
        /// <typeparam name="T">INetSerializable class</typeparam>
        public void RegisterNestedType<T>(Func<T> constructor) where T : class, INetSerializable
        {
            _netSerializer.RegisterNestedType(constructor);
        }

        /// <summary>
        /// Reads one packet from NetDataReader and calls OnReceive delegate
        /// </summary>
        /// <param name="reader">NetDataReader with packet</param>
        /// <exception cref="ParseException">Malformed packet</exception>
        public void ReadPacket(NetDataReader reader)
        {
            ReadPacket(reader, null);
        }

        public void Write<T>(NetDataWriter writer, T packet) where T : class, new()
        {
            WritePacketId<T>(writer);
            _netSerializer.Serialize(writer, packet);
        }

        /// <summary>
        /// Reads one packet from NetDataReader and calls OnReceive delegate
        /// </summary>
        /// <param name="reader">NetDataReader with packet</param>
        /// <param name="userData">Argument that passed to OnReceivedEvent</param>
        /// <exception cref="ParseException">Malformed packet</exception>
        public void ReadPacket(NetDataReader reader, object userData)
        {
            GetCallbackFromData(reader)(reader, userData);
        }

        /// <summary>
        /// Register and subscribe to packet receive event
        /// </summary>
        /// <param name="onReceive">event that will be called when packet deserialized with ReadPacket method</param>
        /// <param name="packetConstructor">Method that constructs packet instead of slow Activator.CreateInstance</param>
        /// <exception cref="InvalidTypeException"><typeparamref name="T"/>'s fields are not supported, or it has no fields</exception>
        public void Subscribe<T>(Action<T> onReceive, Func<T> packetConstructor) where T : class, new()
        {
            _netSerializer.Register<T>();
            _callbacks.Add(GetPacketId<T>(), (reader, userData) =>
            {
                var reference = packetConstructor();
                if (!_netSerializer.Deserialize(reader, reference))
                    throw new ParseException("Malformed packet payload");
                onReceive(reference);
            });
        }

        /// <summary>
        /// Register and subscribe to packet receive event (with userData)
        /// </summary>
        /// <param name="onReceive">event that will be called when packet deserialized with ReadPacket method</param>
        /// <param name="packetConstructor">Method that constructs packet instead of slow Activator.CreateInstance</param>
        /// <exception cref="InvalidTypeException"><typeparamref name="T"/>'s fields are not supported, or it has no fields</exception>
        public void Subscribe<T, TUserData>(Action<T, TUserData> onReceive, Func<T> packetConstructor) where T : class, new()
        {
            _netSerializer.Register<T>();
            _callbacks.Add(GetPacketId<T>(), (reader, userData) =>
            {
                var reference = packetConstructor();
                if (!_netSerializer.Deserialize(reader, reference))
                    throw new ParseException("Malformed packet payload");
                onReceive(reference, (TUserData)userData);
            });
        }

        /// <summary>
        /// Register and subscribe to packet receive event
        /// This method will overwrite last received packet class on receive (less garbage)
        /// </summary>
        /// <param name="onReceive">event that will be called when packet deserialized with ReadPacket method</param>
        /// <exception cref="InvalidTypeException"><typeparamref name="T"/>'s fields are not supported, or it has no fields</exception>
        public void SubscribeReusable<T>(Action<T> onReceive) where T : class, new()
        {
            _netSerializer.Register<T>();
            var reference = new T();
            _callbacks.Add(GetPacketId<T>(), (reader, userData) =>
            {
                if (!_netSerializer.Deserialize(reader, reference))
                    throw new ParseException("Malformed packet payload");
                onReceive(reference);
            });
        }

        /// <summary>
        /// Register and subscribe to packet receive event
        /// This method will overwrite last received packet class on receive (less garbage)
        /// </summary>
        /// <param name="onReceive">event that will be called when packet deserialized with ReadPacket method</param>
        /// <exception cref="InvalidTypeException"><typeparamref name="T"/>'s fields are not supported, or it has no fields</exception>
        public void SubscribeReusable<T, TUserData>(Action<T, TUserData> onReceive) where T : class, new()
        {
            _netSerializer.Register<T>();
            var reference = new T();
            _callbacks.Add(GetPacketId<T>(), (reader, userData) =>
            {
                if (!_netSerializer.Deserialize(reader, reference))
                    throw new ParseException("Malformed packet payload");
                onReceive(reference, (TUserData)userData);
            });
        }
    }
}
