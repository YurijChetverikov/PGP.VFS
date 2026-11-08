namespace PGP.VFS
{
    public partial class VFS 
    {
        public partial class File : IFile
        {
            public Guid Id { get; private set; }
            public Metadata Metadata { get; private set; }
            public string SourceRelativePath { get; private set; }
            public VFSFileFlags Flags { get; private set; }
            public byte PackIndex { get; private set; }
            public uint PackOffset { get; private set; }


            Guid IFile.Id
            {
                get { return this.Id; }
                set { this.Id = value; }
            }
            Metadata IFile.Metadata
            {
                get { return this.Metadata; }
                set { this.Metadata = value; }
            }
            string IFile.SourceRelativePath
            {
                get { return this.SourceRelativePath; }
                set { this.SourceRelativePath = value; }
            }
            VFSFileFlags IFile.Flags
            {
                get { return this.Flags; }
                set { this.Flags = value; }
            }
            byte IFile.PackIndex
            {
                get { return this.PackIndex; }
                set { this.PackIndex = value; }
            }
            uint IFile.PackOffset
            {
                get { return this.PackOffset; }
                set { this.PackOffset = value; }
            }


            public File(Guid id, Metadata metadata, string sourceRelativePath, VFSFileFlags flags, byte packIndex, uint packOffset)
            {
                this.Id = id;
                this.Metadata = metadata;
                this.SourceRelativePath = sourceRelativePath;
                this.Flags = flags;
                this.PackOffset = packOffset;
                this.PackIndex = packIndex;
            }
        }
    }
}
