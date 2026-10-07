using System.Configuration;
using System.Data;
using System.Windows;

namespace TANGERINE_PhotoViewer
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        static App()
        {
            // Apply ImageMagick cache limits before any decoder initializes.
            // The map limit has no public Magick.NET setter.
            Environment.SetEnvironmentVariable("MAGICK_MEMORY_LIMIT", "256MiB",
                EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("MAGICK_MAP_LIMIT", "256MiB",
                EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("MAGICK_AREA_LIMIT", "64MP",
                EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("MAGICK_DISK_LIMIT", "64GiB",
                EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("MAGICK_THREAD_LIMIT", "4",
                EnvironmentVariableTarget.Process);
        }
    }

}
