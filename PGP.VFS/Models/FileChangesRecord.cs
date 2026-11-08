namespace PGP.VFS
{
    internal class FileChangesRecord
    {
        internal Guid Id { get; init; }
        internal VFSFileFlags Flags { get; init; }
        internal uint CompressedSize { get; set; }
        internal uint UncompressedSize { get; set; }
        internal uint PackOffset { get; init; }
        internal byte PackIndex { get; init; }
    }
}
