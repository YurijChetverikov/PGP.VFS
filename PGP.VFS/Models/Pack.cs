namespace PGP.VFS
{
    public partial class VFS
    {
        public partial class Pack : IPack
        {
            public byte Index { get; private set; }
            public uint FreeOffset { get; private set; }

            byte IPack.Index
            {
                get { return this.Index; }
                set { this.Index = value; }
            }
            uint IPack.FreeOffset
            {
                get { return this.FreeOffset; }
                set { this.FreeOffset = value; }
            }

            public Pack(byte index, uint freeOffset)
            {
                this.Index = index;
                this.FreeOffset = freeOffset;
            }
        }
    }
}
