namespace PGP.VFS
{
    public partial class VFS
    {
        private partial interface IMetadata
        {
            DateTime CreatedUtc { get; set; }
            DateTime UpdatedUtc { get; set; }
            uint UncompressedSize { get; set; }
            uint CompressedSize { get; set; }
        }
    }
}
