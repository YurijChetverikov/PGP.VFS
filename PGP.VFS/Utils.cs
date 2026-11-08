namespace PGP.VFS
{
    internal static class Utils
    {
        internal static (long a, long b) ConvertGUID(Guid guid)
        {
            byte[] bytes = guid.ToByteArray();
            long first = BitConverter.ToInt64(bytes, 0);
            long second = BitConverter.ToInt64(bytes, 8);

            return (first, second);
        }

        internal static (long a, long b) ConvertGUID(byte[] bytes)
        {
            long first = BitConverter.ToInt64(bytes, 0);
            long second = BitConverter.ToInt64(bytes, 8);

            return (first, second);
        }

        internal static byte[] ConvertGUID(long a, long b)
        {
            byte[] bytes = new byte[16];

            BitConverter.GetBytes(a).CopyTo(bytes, 0);
            BitConverter.GetBytes(b).CopyTo(bytes, 8);

            return bytes;
        }




        //--------------------------VFS File Flags

        // Set flag
        public static VFSFileFlags Set(this VFSFileFlags value, VFSFileFlags flag)
            => value | flag;

        // Clear flag
        public static VFSFileFlags Clear(this VFSFileFlags value, VFSFileFlags flag)
            => value & ~flag;

        // Toggle flag
        public static VFSFileFlags Toggle(this VFSFileFlags value, VFSFileFlags flag)
            => value ^ flag;

        // Check if flag is set
        public static bool Has(this VFSFileFlags value, VFSFileFlags flag)
            => (value & flag) == flag;
    }
}
