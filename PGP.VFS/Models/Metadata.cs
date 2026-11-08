namespace PGP.VFS
{
    public partial class VFS 
    {
        public partial class Metadata : IMetadata
        {
            public DateTime CreatedUtc { get; private set; }
            public DateTime UpdatedUtc { get; private set; }
            public uint UncompressedSize { get; private set; }
            public uint CompressedSize { get; private set; }

            DateTime IMetadata.CreatedUtc
            {
                get { return this.CreatedUtc; }
                set { this.CreatedUtc = value; }
            }
            DateTime IMetadata.UpdatedUtc
            {
                get { return this.UpdatedUtc; }
                set { this.UpdatedUtc = value; }
            }
            uint IMetadata.CompressedSize
            {
                get { return this.CompressedSize; }
                set { this.CompressedSize = value; }
            }
            uint IMetadata.UncompressedSize
            {
                get { return this.UncompressedSize; }
                set { this.UncompressedSize = value; }
            }

            public Metadata(DateTime createdUtc, DateTime updatedUtc, uint uncompressedSize, uint compressedSize)
            {
                this.CreatedUtc = createdUtc;
                this.UpdatedUtc = updatedUtc;
                this.UncompressedSize = uncompressedSize;
                this.CompressedSize = compressedSize;
            }
        }
    }
}
