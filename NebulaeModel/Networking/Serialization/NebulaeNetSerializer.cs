using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Net;
using NebulaeModel.Contracts;

namespace NebulaeModel.Networking.Serialization;

internal class NebulaeNetSerializer : NetSerializer
{
    private static readonly Dictionary<Type, Delegate> readers = new();
    private static readonly Dictionary<Type, Delegate> writers = new();

    static NebulaeNetSerializer()
    {
        Add<bool>(r => r.GetBool(), (w, v) => w.Put(v));
        Add<byte>(r => r.GetByte(), (w, v) => w.Put(v));
        Add<sbyte>(r => r.GetSByte(), (w, v) => w.Put(v));
        Add<short>(r => r.GetShort(), (w, v) => w.Put(v));
        Add<ushort>(r => r.GetUShort(), (w, v) => w.Put(v));
        Add<int>(r => r.GetInt(), (w, v) => w.Put(v));
        Add<uint>(r => r.GetUInt(), (w, v) => w.Put(v));
        Add<long>(r => r.GetLong(), (w, v) => w.Put(v));
        Add<ulong>(r => r.GetULong(), (w, v) => w.Put(v));
        Add<float>(r => r.GetFloat(), (w, v) => w.Put(v));
        Add<double>(r => r.GetDouble(), (w, v) => w.Put(v));
        Add<char>(r => r.GetChar(), (w, v) => w.Put(v));
        Add<string>(r => r.GetString(), (w, v) => w.Put(v));
        Add<IPEndPoint>(r => r.GetNetEndPoint(), (w, v) => w.Put(v));
        Add<bool[]>(r => r.GetBoolArray(), (w, v) => w.PutArray(v));
        Add<byte[]>(r => r.GetBytesWithLength(), (w, v) => w.PutBytesWithLength(v));
        Add<sbyte[]>(r => r.GetSBytesWithLength(), (w, v) => w.PutSBytesWithLength(v));
        Add<short[]>(r => r.GetShortArray(), (w, v) => w.PutArray(v));
        Add<ushort[]>(r => r.GetUShortArray(), (w, v) => w.PutArray(v));
        Add<int[]>(r => r.GetIntArray(), (w, v) => w.PutArray(v));
        Add<uint[]>(r => r.GetUIntArray(), (w, v) => w.PutArray(v));
        Add<long[]>(r => r.GetLongArray(), (w, v) => w.PutArray(v));
        Add<ulong[]>(r => r.GetULongArray(), (w, v) => w.PutArray(v));
        Add<float[]>(r => r.GetFloatArray(), (w, v) => w.PutArray(v));
        Add<double[]>(r => r.GetDoubleArray(), (w, v) => w.PutArray(v));
        Add<string[]>(r => r.GetStringArray(), (w, v) => w.PutArray(v));
    }

    private static void Add<T>(Func<NetDataReader, T> read, Action<NetDataWriter, T> write)
    {
        readers.Add(typeof(T), read);
        writers.Add(typeof(T), write);
    }

    public override void Register<T>() => RegisterInternal<T>();

    // The method token comes from a typed expression; it never depends on a name.
    private static readonly System.Reflection.MethodInfo dictionaryRegistration =
        ((MethodCallExpression)((Expression<Action<NebulaeNetSerializer>>)(s =>
            s.RegisterDictionary<Dictionary<int, int>, int, int>())).Body).Method.GetGenericMethodDefinition();

    protected override ClassInfo<T> RegisterInternal<T>()
    {
        if (ClassInfo<T>.Instance != null) return ClassInfo<T>.Instance;
        foreach (var property in WireContract.Properties(typeof(T)))
        {
            var type = property.PropertyType;
            if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(Dictionary<,>) ||
                _registeredTypes.ContainsKey(type)) continue;
            var arguments = type.GetGenericArguments();
            dictionaryRegistration.MakeGenericMethod(type, arguments[0], arguments[1]).Invoke(this, null);
        }
        return base.RegisterInternal<T>();
    }

    private void RegisterDictionary<TDict, TKey, TValue>()
        where TDict : Dictionary<TKey, TValue> where TKey : new() where TValue : new()
    {
        var readKey = Getter<TKey>();
        var writeKey = Putter<TKey>();
        var readValue = Getter<TValue>();
        var writeValue = Putter<TValue>();
        RegisterNestedType<TDict>((writer, dictionary) =>
        {
            writer.Put(dictionary.Count);
            foreach (var entry in dictionary)
            {
                writeKey(writer, entry.Key);
                writeValue(writer, entry.Value);
            }
        }, reader =>
        {
            var count = reader.GetInt();
            if (count < 0 || count > reader.AvailableBytes)
                throw new ParseException("Invalid dictionary element count");
            var result = new Dictionary<TKey, TValue>();
            for (var i = 0; i < count; i++) result.Add(readKey(reader), readValue(reader));
            return (TDict)result;
        });
    }

    private Func<NetDataReader, T> Getter<T>() where T : new()
    {
        if (readers.TryGetValue(typeof(T), out var read)) return (Func<NetDataReader, T>)read;
        if (typeof(INetSerializable).IsAssignableFrom(typeof(T)))
            return reader =>
            {
                // A single box retains mutations made by a struct's Deserialize.
                object value = new T();
                ((INetSerializable)value).Deserialize(reader);
                return (T)value;
            };
        if (typeof(T).IsValueType || typeof(T).IsArray)
            throw new InvalidTypeException("Unsupported dictionary value: " + typeof(T));
        var readCustom = RegisterInternal<T>().Read;
        return reader => { var value = new T(); readCustom(value, reader); return value; };
    }

    private Action<NetDataWriter, T> Putter<T>()
    {
        if (writers.TryGetValue(typeof(T), out var write)) return (Action<NetDataWriter, T>)write;
        if (typeof(INetSerializable).IsAssignableFrom(typeof(T)))
            return (writer, value) => ((INetSerializable)(object)value).Serialize(writer);
        if (typeof(T).IsValueType || typeof(T).IsArray)
            throw new InvalidTypeException("Unsupported dictionary value: " + typeof(T));
        var writeCustom = RegisterInternal<T>().Write;
        return (writer, value) => writeCustom(value, writer);
    }
}
