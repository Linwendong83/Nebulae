namespace NebulaeTests.Fixtures;

internal sealed class BrokenStream : MemoryStream
{
    public override void Write(byte[] buffer, int offset, int count) => throw new IOException("fixture write failure");
}
