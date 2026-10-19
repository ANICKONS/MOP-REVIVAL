// MOP - REVIVAL
// Replacement for the original Visual Studio generated Resources.Designer.cs.
// SDK-style builds cannot serialize binary .resx entries for .NET 3.5, so the resources are embedded
// directly (see MOPR.csproj) and exposed through the same static API the original code uses.

using System.IO;
using System.Reflection;
using System.Text;

namespace MOP.Properties
{
    internal static class Resources
    {
        private static byte[] iconCache;

        internal static byte[] icon => iconCache ?? (iconCache = ReadBytes("MOPR.Resources.icon"));
        internal static string changelog => ReadText("MOPR.Resources.changelog.txt");
        internal static string itemcounter_to_transform => ReadText("MOPR.Resources.itemcounter_to_transform.csv");

        private static byte[] ReadBytes(string name)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) return new byte[0];
                byte[] data = new byte[s.Length];
                int offset = 0, read;
                while (offset < data.Length && (read = s.Read(data, offset, data.Length - offset)) > 0)
                    offset += read;
                return data;
            }
        }

        private static string ReadText(string name)
        {
            byte[] bytes = ReadBytes(name);
            return bytes.Length == 0 ? string.Empty : Encoding.UTF8.GetString(bytes);
        }
    }
}
