namespace NebulaeModel.Authority;

internal enum RuntimeObjectIntegrity : byte
{
    Unavailable,
    Matches,
    Drift
}

internal interface IReplicaRuntimeVerifier
{
    RuntimeObjectIntegrity VerifyObject(ObjectKey key, byte[] committedState);
}
