namespace PGP.VFS
{
    public partial class VFS
    {
        private interface IPack
        {
            byte Index { get; set; }
            uint FreeOffset { get; set; }
        }
    }
}
