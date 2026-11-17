namespace NyaFs.Filesystem.CramFs
{
    public interface ICramFsEndian
    {
        bool BigEndian { get; }
        bool WideInodes { get; }
    }
}
