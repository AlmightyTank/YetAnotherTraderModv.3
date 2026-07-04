using System.IO;
using System.Reflection;

namespace YetAnotherTraderMod.Client.Services
{
    public static class PluginPathService
    {
        public static string PluginDirectory
        {
            get
            {
                var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

                if (string.IsNullOrWhiteSpace(pluginDir))
                {
                    pluginDir = Directory.GetCurrentDirectory();
                }

                return pluginDir;
            }
        }

        public static string GetPluginFilePath(string fileName)
        {
            return Path.Combine(PluginDirectory, fileName);
        }

        public static string TryGetSptRoot()
        {
            var dir = new DirectoryInfo(PluginDirectory);

            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "BepInEx"))
                    && Directory.Exists(Path.Combine(dir.FullName, "EscapeFromTarkov_Data")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            return Directory.GetCurrentDirectory();
        }
    }
}
