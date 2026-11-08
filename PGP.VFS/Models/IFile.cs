namespace PGP.VFS
{
    public partial class VFS
    {
        private partial interface IFile
        {
            Guid Id { get; set; }
            Metadata Metadata { get; set; }
            string SourceRelativePath { get; set; }
            VFSFileFlags Flags { get; set; }
            byte PackIndex { get; set; }
            uint PackOffset { get; set; }
        }
    }
}
